// Verifies Cloudflare Access JWTs (the `Cf-Access-Jwt-Assertion` header).
//
// This is Cloudflare's documented approach: fetch the team's JWKS and verify the
// assertion's signature + iss + aud with `jose`. The bare
// `Cf-Access-Authenticated-User-Email` header is spoofable and MUST NOT be
// trusted without this verification.
import { createRemoteJWKSet, jwtVerify } from "jose";
import type { Env } from "./env";

export interface AccessIdentity {
  email: string;
  sub?: string;
}

// Cache the JWKS set per team domain (module scope survives across requests in
// the same isolate). createRemoteJWKSet handles its own key caching/refresh.
const jwksCache = new Map<string, ReturnType<typeof createRemoteJWKSet>>();

function jwksFor(teamDomain: string): ReturnType<typeof createRemoteJWKSet> {
  let set = jwksCache.get(teamDomain);
  if (!set) {
    const url = new URL(`https://${teamDomain}.cloudflareaccess.com/cdn-cgi/access/certs`);
    set = createRemoteJWKSet(url);
    jwksCache.set(teamDomain, set);
  }
  return set;
}

// Verifies the Access assertion on a request. Returns the identity, or null if
// absent/invalid. Set `throwError` to distinguish "no header" from "bad header".
export async function verifyAccessJwt(
  request: Request,
  env: Env,
): Promise<AccessIdentity | null> {
  const assertion =
    request.headers.get("Cf-Access-Jwt-Assertion") ??
    request.headers.get("cf-access-jwt-assertion");
  if (!assertion) return null;

  const issuer = `https://${env.ACCESS_TEAM_DOMAIN}.cloudflareaccess.com`;
  try {
    const { payload } = await jwtVerify(assertion, jwksFor(env.ACCESS_TEAM_DOMAIN), {
      issuer,
      audience: env.ACCESS_AUD,
    });
    const email = (payload.email as string | undefined) ?? "";
    if (!email) return null;
    return { email, sub: payload.sub };
  } catch {
    return null;
  }
}

// Resolves the Access-authenticated email for gated routes (/approve, /admin/*).
//
// In production this always requires a verified Access JWT. In local dev/test —
// where real Access can't be exercised — an env-gated bypass accepts a
// dev-supplied email header or `?dev_email=` query param. The bypass requires
// BOTH ACCESS_DEV_BYPASS AND ENVIRONMENT === "development" — a two-key gate so
// a lone stray ACCESS_DEV_BYPASS (e.g. copy-pasted into the wrong wrangler env)
// can never silently disable real Access verification in production. Neither
// var may ever be set in production wrangler.toml.
export async function resolveAccessEmail(
  request: Request,
  env: Env,
): Promise<string | null> {
  const identity = await verifyAccessJwt(request, env);
  if (identity) return identity.email;

  if (env.ACCESS_DEV_BYPASS) {
    if (env.ENVIRONMENT !== "development") {
      // Loud, not silent: this combination means either a misconfigured
      // deploy or an active attempt to exploit the bypass — either way it
      // must be visible in the Worker's logs, not swallowed.
      console.error(
        "ACCESS_DEV_BYPASS is set but ENVIRONMENT is not 'development' — refusing the bypass. " +
          "Check wrangler.toml / environment vars for a misconfiguration.",
      );
      return null;
    }
    const headerEmail = request.headers.get("X-Dev-Access-Email");
    if (headerEmail) return headerEmail;
    const url = new URL(request.url);
    const queryEmail = url.searchParams.get("dev_email");
    if (queryEmail) return queryEmail;
  }
  return null;
}
