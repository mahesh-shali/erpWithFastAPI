import os
from functools import lru_cache
from pathlib import Path
from urllib.parse import quote, urlparse

from pydantic_settings import BaseSettings, PydanticBaseSettingsSource, SettingsConfigDict

# ai-service/.env, found from this file's location so it works from any working directory.
# AI_SERVICE_ENV_FILE overrides it: a path to another file, or "none" to read only real environment
# variables (run-local.sh does this so .env.development / .env.production decide the settings).
_env_file_override = os.environ.get("AI_SERVICE_ENV_FILE", "").strip()
ENV_FILE: Path | None = (
    None if _env_file_override.lower() == "none"
    else Path(_env_file_override) if _env_file_override
    else Path(__file__).resolve().parent.parent / ".env"
)


class Settings(BaseSettings):
    model_config = SettingsConfigDict(env_file=ENV_FILE, extra="ignore")

    @classmethod
    def settings_customise_sources(cls, settings_cls, init_settings: PydanticBaseSettingsSource,
                                   env_settings: PydanticBaseSettingsSource,
                                   dotenv_settings: PydanticBaseSettingsSource,
                                   file_secret_settings: PydanticBaseSettingsSource):
        # ai-service/.env beats process env vars. Locally, editor terminals can carry stale injected values
        # (Docker hostnames, placeholder keys) that would silently override the file. In containers and on
        # Render there is no .env (.dockerignore excludes it), so real environment variables apply as usual.
        return init_settings, dotenv_settings, env_settings, file_secret_settings

    app_env: str = "development"           # development | production
    cors_origins: str = "http://localhost:5173,http://localhost"  # comma-separated

    database_url: str = "postgresql://erp_ai_ro:ai_readonly_dev@localhost:5432/erp"
    redis_url: str = "redis://localhost:6379/0"
    # Upstash: the REST URL/token pair from the console. When both are set they win over REDIS_URL;
    # the REST token is also the Redis password, so we connect over TLS to <host>:6379.
    upstash_redis_rest_url: str = ""
    upstash_redis_rest_token: str = ""

    jwt_secret: str = "change-me-to-a-random-256-bit-secret-value-0123456789"
    jwt_issuer: str = "erp-api"
    jwt_audience: str = "erp-clients"

    groq_api_key: str = ""
    groq_model: str = "openai/gpt-oss-120b"
    llm_max_tokens: int = 4096             # incl. reasoning tokens on reasoning models
    llm_max_concurrency: int = 64          # per pod; protects Groq rate limits
    agent_max_steps: int = 5               # max tool-use round trips per question

    max_result_rows: int = 500             # rows returned to the UI
    rows_sent_to_model: int = 50           # rows the LLM sees (cost + privacy)
    db_pool_min: int = 2
    db_pool_max: int = 20

    rate_limit_per_minute: int = 20        # per user
    answer_cache_ttl_seconds: int = 120
    session_check_ttl_seconds: int = 30    # how long a "session active?" answer is cached

    @property
    def is_production(self) -> bool:
        return self.app_env.lower() == "production"

    @property
    def effective_redis_url(self) -> str:
        if self.upstash_redis_rest_url and self.upstash_redis_rest_token:
            host = urlparse(self.upstash_redis_rest_url).hostname
            if not host:
                raise ValueError("UPSTASH_REDIS_REST_URL must look like https://<name>.upstash.io")
            return f"rediss://default:{quote(self.upstash_redis_rest_token, safe='')}@{host}:6379/0"
        return self.redis_url

    @property
    def cors_origin_list(self) -> list[str]:
        return [o.strip() for o in self.cors_origins.split(",") if o.strip()]

    def check_production(self) -> None:
        """Fail fast if production is running with dev defaults."""
        if not self.is_production:
            return
        problems = []
        if self.jwt_secret.startswith("change-me"):
            problems.append("JWT_SECRET is the placeholder")
        if not self.groq_api_key:
            problems.append("GROQ_API_KEY is empty")
        if "ai_readonly_dev" in self.database_url:
            problems.append("DATABASE_URL uses the dev AI password")
        if problems:
            raise RuntimeError("Unsafe production config: " + "; ".join(problems))


@lru_cache
def get_settings() -> Settings:
    return Settings()
