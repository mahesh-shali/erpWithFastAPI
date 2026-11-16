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


# ---------------- production guard rails ----------------
import pytest  # noqa: E402

SAFE = dict(app_env="production", jwt_secret="x" * 40, groq_api_key="gsk_real",
            database_url="postgresql://erp_ai_ro_prod:strong@db:5432/erp_prod")


def test_production_with_safe_settings_starts():
    Settings(_env_file=None, **SAFE).check_production()


@pytest.mark.parametrize(("override", "message"), [
    ({"jwt_secret": "change-me-please-0123456789-0123456789"}, "JWT_SECRET is the placeholder"),
    ({"groq_api_key": ""}, "GROQ_API_KEY is empty"),
    ({"database_url": "postgresql://erp_ai_ro:ai_readonly_dev@db:5432/erp"}, "DATABASE_URL uses the dev AI password"),
])
def test_production_refuses_dev_defaults(override, message):
    with pytest.raises(RuntimeError, match=message):
        Settings(_env_file=None, **{**SAFE, **override}).check_production()


def test_production_lists_every_problem_at_once():
    s = Settings(_env_file=None, **{**SAFE, "jwt_secret": "change-me-" + "x" * 30, "groq_api_key": ""})
    with pytest.raises(RuntimeError) as e:
        s.check_production()
    assert "JWT_SECRET" in str(e.value) and "GROQ_API_KEY" in str(e.value)


def test_development_skips_the_checks():
    Settings(_env_file=None, app_env="development", jwt_secret="change-me-" + "x" * 30, groq_api_key="").check_production()


def test_is_production_ignores_case():
    assert Settings(_env_file=None, app_env="Production").is_production


def test_cors_origins_are_split_and_trimmed():
    s = Settings(_env_file=None, cors_origins=" http://a.com, ,http://b.com ")
    assert s.cors_origin_list == ["http://a.com", "http://b.com"]


def test_bad_upstash_url_is_rejected():
    s = Settings(_env_file=None, upstash_redis_rest_url="not a url", upstash_redis_rest_token="t")
    with pytest.raises(ValueError, match="upstash"):
        _ = s.effective_redis_url
