// AI chat load test. Mostly repeated questions -> exercises the Redis answer cache,
// plus a small share of unique questions that really hit the LLM on Groq (uses rate limit / tokens!).
//   k6 run -e BASE_URL=http://localhost -e TOKEN=<jwt> load-tests/k6/chat.js
import http from 'k6/http';
import { check, sleep } from 'k6';
import { Trend, Rate } from 'k6/metrics';

const BASE = __ENV.BASE_URL || 'http://localhost';
const COMMON = [
  'What is my total revenue?',
  'How many customers do I have?',
  'Top 5 products by quantity sold',
  'Which city has the most customers?',
];

const cachedLatency = new Trend('chat_cached_ms', true);
const liveLatency = new Trend('chat_live_ms', true);
const rateLimited = new Rate('chat_rate_limited');

export const options = {
  vus: Number(__ENV.VUS || 50),
  duration: __ENV.DURATION || '2m',
  thresholds: {
    chat_cached_ms: ['p(95)<150'],
    chat_live_ms: ['p(95)<20000'],
    checks: ['rate>0.95'],
  },
};

export default function () {
  const unique = Math.random() < (Number(__ENV.UNIQUE_RATIO) || 0.02);
  const question = unique
    ? `How many orders were placed in the last ${1 + Math.floor(Math.random() * 365)} days?`
    : COMMON[Math.floor(Math.random() * COMMON.length)];

  const res = http.post(`${BASE}/ai/chat`, JSON.stringify({ question, history: [] }), {
    headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${__ENV.TOKEN}` },
    timeout: '60s',
  });

  rateLimited.add(res.status === 429);
  if (res.status === 200) {
    (res.json('cached') ? cachedLatency : liveLatency).add(res.timings.duration);
  }
  check(res, { 'chat ok or rate-limited': (r) => r.status === 200 || r.status === 429 });
  sleep(2);
}
