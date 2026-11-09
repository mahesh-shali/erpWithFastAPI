from functools import lru_cache

from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    model_config = SettingsConfigDict(env_file=".env", extra="ignore")

    app_env: str = "development"           # development | production
    cors_origins: str = "http://localhost:5173,http://localhost"  # comma-separated

    database_url: str = "postgresql://erp_ai_ro:ai_readonly_dev@localhost:5432/erp"
    redis_url: str = "redis://localhost:6379/0"

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
