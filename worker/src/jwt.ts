// Our own short-lived access-token JWTs (HS256), signed with JWT_HMAC_SECRET.
// Thin wrapper around @tsndr/cloudflare-worker-jwt so the claim shape and TTL
// live in one place.
import jwt from "@tsndr/cloudflare-worker-jwt";
import { newId } from "./crypto";

export const ACCESS_TOKEN_TTL_SEC = 900; // 15 minutes

export interface AppJwtClaims {
  sub: string; // user_id
  tier: string;
  app_id: string;
  jti: string;
  iat: number;
  exp: number;
}

export async function signAccessToken(
  secret: string,
  input: { user_id: string; tier: string; app_id: string },
): Promise<{ token: string; claims: AppJwtClaims }> {
  const iat = Math.floor(Date.now() / 1000);
  const claims: AppJwtClaims = {
    sub: input.user_id,
    tier: input.tier,
    app_id: input.app_id,
    jti: newId(),
    iat,
    exp: iat + ACCESS_TOKEN_TTL_SEC,
  };
  const token = await jwt.sign(claims, secret, { algorithm: "HS256" });
  return { token, claims };
}

// Returns the verified claims, or null if the token is missing/invalid/expired.
export async function verifyAccessToken(
  secret: string,
  token: string,
): Promise<AppJwtClaims | null> {
  try {
    const result = await jwt.verify<AppJwtClaims>(token, secret, { algorithm: "HS256" });
    if (!result) return null;
    const payload = result.payload as AppJwtClaims;
    // Guard the subject claim: everything downstream namespaces tenant data by
    // `sub`, so a token missing (or with a non-string/empty) sub must never be
    // treated as authenticated.
    if (typeof payload?.sub !== "string" || payload.sub.length === 0) return null;
    return payload;
  } catch {
    return null;
  }
}

// Pulls the bearer token out of an Authorization header.
export function bearerFrom(request: Request): string | null {
  const header = request.headers.get("Authorization") ?? "";
  const match = /^Bearer\s+(.+)$/i.exec(header.trim());
  return match ? match[1] : null;
}
