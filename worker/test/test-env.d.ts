/// <reference types="@cloudflare/vitest-pool-workers/types" />
/// <reference types="vite/client" />

import type { Env } from "../src/env";

// Bind the test env to our Env shape so `import { env } from "cloudflare:test"`
// is typed.
declare module "cloudflare:test" {
  interface ProvidedEnv extends Env {}
}

// The Vite `?raw` import suffix used to load schema.sql into tests is declared
// by vite/client (referenced above).
