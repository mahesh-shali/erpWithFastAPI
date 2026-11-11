from app.config import Settings


def test_env_file_wins_over_stale_environment_variables(tmp_path, monkeypatch):
    env = tmp_path / ".env"
    env.write_text("GROQ_API_KEY=gsk_from_file\n")
    monkeypatch.setenv("GROQ_API_KEY", "gsk_...")
    monkeypatch.setenv("GROQ_MODEL", "from-env")
    s = Settings(_env_file=env)
    assert s.groq_api_key == "gsk_from_file"
    assert s.groq_model == "from-env"  # not in the file: env var still applies


def test_redis_url_used_when_no_upstash():
    s = Settings(_env_file=None, redis_url="redis://localhost:6380/0")
    assert s.effective_redis_url == "redis://localhost:6380/0"


def test_upstash_rest_settings_become_tls_redis_url_and_win_over_redis_url():
    s = Settings(_env_file=None, redis_url="redis://redis:6379/0",
                 upstash_redis_rest_url="https://pleased-cat-1.upstash.io", upstash_redis_rest_token="tok/en+1")
    assert s.effective_redis_url == "rediss://default:tok%2Fen%2B1@pleased-cat-1.upstash.io:6379/0"
