// conn-soak: holds a large number of concurrent keep-alive HTTP connections against the stack and sends a
// light request on each one every INTERVAL. This is what "N concurrent connections" means for a server:
// N open sockets, each occasionally active — not N users hammering in a tight loop (that is k6's job).
//
// One instance is limited by its source ports (~64k with the widened port range), so run several
// replicas (each container has its own IP):   ./local.sh conns 100000
//
// Env (per instance):
//   TARGET      host:port to connect to            (default nginx:80 — the real entry point)
//   REQ_PATH    path requested on each connection  (default /health/live: API round trip, no DB)
//   CONNS       connections this instance holds   (default 20000)
//   RAMP        new connections per second         (default 1000)
//   INTERVAL    seconds between requests per conn  (default 30, ±50% jitter)
//   HOLD        how long to hold all connections after the ramp, e.g. 5m (default 5m)
//   METRICS     listen address for /metrics        (default :9100, scraped by Prometheus)
package main

import (
	"bufio"
	"context"
	"fmt"
	"io"
	"log"
	"math/rand"
	"net"
	"net/http"
	"os"
	"os/signal"
	"strconv"
	"sync"
	"sync/atomic"
	"syscall"
	"time"
)

var (
	target   = env("TARGET", "nginx:80")
	reqPath  = env("REQ_PATH", "/health/live")
	conns    = envInt("CONNS", 20000)
	ramp     = envInt("RAMP", 1000)
	interval = time.Duration(envInt("INTERVAL", 30)) * time.Second
	hold     = envDur("HOLD", 5*time.Minute)
	metrics  = env("METRICS", ":9100")
	host, _  = os.Hostname()
)

// Latency histogram buckets (seconds), Prometheus-style cumulative on output.
var buckets = []float64{0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10}

var (
	open, dials, dialErrs, reqs, reqErrs, non2xx, reconnects atomic.Int64
	bucketCounts                                             = make([]atomic.Int64, len(buckets)+1)
	latSumMicros                                             atomic.Int64
	statusMu                                                 sync.Mutex
	statuses                                                 = map[int]int64{}
)

func statusSummary() string {
	statusMu.Lock()
	defer statusMu.Unlock()
	out := ""
	for code, n := range statuses {
		out += fmt.Sprintf(" %d:%d", code, n)
	}
	if out == "" {
		return " -"
	}
	return out
}

func main() {
	log.SetFlags(0)
	log.SetPrefix(fmt.Sprintf("[%s] ", host))
	ctx, cancel := signal.NotifyContext(context.Background(), syscall.SIGINT, syscall.SIGTERM)
	defer cancel()

	go serveMetrics()
	log.Printf("target=%s path=%s conns=%d ramp=%d/s interval=%s hold=%s", target, reqPath, conns, ramp, interval, hold)

	reqBytes := []byte("GET " + reqPath + " HTTP/1.1\r\nHost: " + target + "\r\nUser-Agent: conn-soak\r\n\r\n")
	var wg sync.WaitGroup
	go report(ctx)

	// Ramp: start `ramp` connections per second.
	tick := time.NewTicker(time.Second / time.Duration(max(ramp, 1)))
	for i := 0; i < conns; i++ {
		select {
		case <-ctx.Done():
			i = conns
			continue
		case <-tick.C:
		}
		wg.Add(1)
		go func() { defer wg.Done(); worker(ctx, reqBytes) }()
	}
	tick.Stop()
	if ctx.Err() == nil {
		log.Printf("ramp done: %d workers started, holding for %s", conns, hold)
		select {
		case <-ctx.Done():
		case <-time.After(hold):
		}
	}
	cancel()
	wg.Wait()
	summary()
}

// worker keeps one connection open, sends a request every interval (jittered), redials on failure.
func worker(ctx context.Context, req []byte) {
	backoff := time.Second
	first := true
	for ctx.Err() == nil {
		d := net.Dialer{Timeout: 10 * time.Second, KeepAlive: 30 * time.Second}
		c, err := d.DialContext(ctx, "tcp", target)
		dials.Add(1)
		if err != nil {
			if ctx.Err() != nil {
				return
			}
			dialErrs.Add(1)
			sleep(ctx, backoff+time.Duration(rand.Int63n(int64(backoff))))
			backoff = min(backoff*2, 30*time.Second)
			continue
		}
		if !first {
			reconnects.Add(1)
		}
		first = false
		backoff = time.Second
		open.Add(1)
		serve(ctx, c, req)
		open.Add(-1)
		c.Close()
	}
}

// serve runs the request loop on one connection until it fails or ctx ends.
func serve(ctx context.Context, c net.Conn, req []byte) {
	go func() { <-ctx.Done(); c.SetDeadline(time.Now()) }() // unblock reads on shutdown
	br := bufio.NewReaderSize(c, 1024)
	wait := time.Duration(rand.Int63n(int64(interval))) // spread the first request over one interval
	for {
		if !sleep(ctx, wait) {
			return
		}
		start := time.Now()
		c.SetDeadline(start.Add(30 * time.Second))
		ok := doRequest(ctx, c, br, req)
		observe(time.Since(start), ok)
		if !ok {
			return
		}
		wait = interval/2 + time.Duration(rand.Int63n(int64(interval)))
	}
}

func doRequest(ctx context.Context, c net.Conn, br *bufio.Reader, req []byte) bool {
	reqs.Add(1)
	fail := func() bool {
		if ctx.Err() == nil { // errors caused by our own shutdown are not server errors
			reqErrs.Add(1)
		}
		return false
	}
	if _, err := c.Write(req); err != nil {
		return fail()
	}
	resp, err := http.ReadResponse(br, nil)
	if err != nil {
		return fail()
	}
	io.Copy(io.Discard, resp.Body)
	resp.Body.Close()
	statusMu.Lock()
	statuses[resp.StatusCode]++
	statusMu.Unlock()
	if resp.StatusCode < 200 || resp.StatusCode > 299 {
		non2xx.Add(1)
	}
	return !resp.Close
}

func observe(d time.Duration, ok bool) {
	if !ok {
		return
	}
	s := d.Seconds()
	i := 0
	for i < len(buckets) && s > buckets[i] {
		i++
	}
	bucketCounts[i].Add(1)
	latSumMicros.Add(d.Microseconds())
}

func report(ctx context.Context) {
	t := time.NewTicker(5 * time.Second)
	defer t.Stop()
	var lastReqs int64
	for {
		select {
		case <-ctx.Done():
			return
		case <-t.C:
		}
		r := reqs.Load()
		log.Printf("open=%d/%d  req/s=%.0f  p95=%s  dial_errors=%d  req_errors=%d  non2xx=%d  reconnects=%d  status:%s",
			open.Load(), conns, float64(r-lastReqs)/5, quantile(0.95), dialErrs.Load(), reqErrs.Load(), non2xx.Load(), reconnects.Load(), statusSummary())
		lastReqs = r
	}
}

func summary() {
	log.Printf("DONE  requests=%d  req_errors=%d  non2xx=%d  dial_errors=%d  reconnects=%d  p50=%s p95=%s p99=%s  status:%s",
		reqs.Load(), reqErrs.Load(), non2xx.Load(), dialErrs.Load(), reconnects.Load(), quantile(0.5), quantile(0.95), quantile(0.99), statusSummary())
}

// quantile returns the upper bound of the bucket holding quantile q.
func quantile(q float64) string {
	var total int64
	for i := range bucketCounts {
		total += bucketCounts[i].Load()
	}
	if total == 0 {
		return "-"
	}
	var cum int64
	for i := range bucketCounts {
		cum += bucketCounts[i].Load()
		if float64(cum) >= q*float64(total) {
			if i == len(buckets) {
				return ">" + fmtSec(buckets[len(buckets)-1])
			}
			return "≤" + fmtSec(buckets[i])
		}
	}
	return "-"
}

func fmtSec(s float64) string { return (time.Duration(s * float64(time.Second))).String() }

func serveMetrics() {
	http.HandleFunc("/metrics", func(w http.ResponseWriter, _ *http.Request) {
		fmt.Fprintf(w, "# TYPE connsoak_open_connections gauge\nconnsoak_open_connections %d\n", open.Load())
		fmt.Fprintf(w, "# TYPE connsoak_target_connections gauge\nconnsoak_target_connections %d\n", conns)
		fmt.Fprintf(w, "# TYPE connsoak_dials_total counter\nconnsoak_dials_total %d\n", dials.Load())
		fmt.Fprintf(w, "# TYPE connsoak_dial_errors_total counter\nconnsoak_dial_errors_total %d\n", dialErrs.Load())
		fmt.Fprintf(w, "# TYPE connsoak_reconnects_total counter\nconnsoak_reconnects_total %d\n", reconnects.Load())
		fmt.Fprintf(w, "# TYPE connsoak_requests_total counter\nconnsoak_requests_total %d\n", reqs.Load())
		fmt.Fprintf(w, "# TYPE connsoak_request_errors_total counter\nconnsoak_request_errors_total %d\n", reqErrs.Load())
		fmt.Fprintf(w, "# TYPE connsoak_non2xx_total counter\nconnsoak_non2xx_total %d\n", non2xx.Load())
		fmt.Fprintf(w, "# TYPE connsoak_responses_total counter\n")
		statusMu.Lock()
		for code, n := range statuses {
			fmt.Fprintf(w, "connsoak_responses_total{code=\"%d\"} %d\n", code, n)
		}
		statusMu.Unlock()
		fmt.Fprintf(w, "# TYPE connsoak_request_duration_seconds histogram\n")
		var cum int64
		for i, b := range buckets {
			cum += bucketCounts[i].Load()
			fmt.Fprintf(w, "connsoak_request_duration_seconds_bucket{le=\"%g\"} %d\n", b, cum)
		}
		cum += bucketCounts[len(buckets)].Load()
		fmt.Fprintf(w, "connsoak_request_duration_seconds_bucket{le=\"+Inf\"} %d\n", cum)
		fmt.Fprintf(w, "connsoak_request_duration_seconds_sum %f\n", float64(latSumMicros.Load())/1e6)
		fmt.Fprintf(w, "connsoak_request_duration_seconds_count %d\n", cum)
	})
	if err := http.ListenAndServe(metrics, nil); err != nil {
		log.Printf("metrics server: %v", err)
	}
}

func sleep(ctx context.Context, d time.Duration) bool {
	t := time.NewTimer(d)
	defer t.Stop()
	select {
	case <-ctx.Done():
		return false
	case <-t.C:
		return true
	}
}

func env(k, def string) string {
	if v := os.Getenv(k); v != "" {
		return v
	}
	return def
}

func envInt(k string, def int) int {
	if v, err := strconv.Atoi(os.Getenv(k)); err == nil && v > 0 {
		return v
	}
	return def
}

func envDur(k string, def time.Duration) time.Duration {
	if v, err := time.ParseDuration(os.Getenv(k)); err == nil && v > 0 {
		return v
	}
	return def
}
