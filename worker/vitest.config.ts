import { defineConfig } from "vitest/config";
import { cloudflareTest } from "@cloudflare/vitest-pool-workers";

// vitest-pool-workers v0.18 (vitest 4) exposes the pool as a Vite plugin rather
// than the old `defineWorkersConfig` / `poolOptions.workers` shape.
export default defineConfig({
  plugins: [
    cloudflareTest({
      wrangler: { configPath: "./wrangler.toml" },
      miniflare: {
        // Test-only bindings. ACCESS_DEV_BYPASS enables the /approve + /admin
        // dev bypass so those flows are testable without real Cloudflare Access.
        // NEVER set this in production wrangler.toml.
        bindings: {
          ACCESS_DEV_BYPASS: "1",
          // Required alongside ACCESS_DEV_BYPASS — see access-verify.ts's two-key gate.
          ENVIRONMENT: "development",
          JWT_HMAC_SECRET: "test-hmac-secret-do-not-use-in-prod",
          ACCESS_TEAM_DOMAIN: "testteam",
          ACCESS_AUD: "test-aud-tag",
          // Admin allowlist for the admin-route tests. Mixed case on purpose —
          // the allowlist check must be case-insensitive.
          ADMIN_EMAILS: "Admin@Example.com, second-admin@example.com",
        },
      },
    }),
  ],
});
