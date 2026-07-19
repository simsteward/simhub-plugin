import { describe, it, expect, beforeAll, afterAll, vi } from "vitest";
import { generateKeyPair, exportJWK, SignJWT } from "jose";
import { verifyAccessJwt, resolveAccessEmail } from "../src/access-verify";
import { testEnv } from "./helpers";

// Matches vitest.config.ts bindings.
const TEAM_DOMAIN = "testteam";
const AUD = "test-aud-tag";
const ISS = `https://${TEAM_DOMAIN}.cloudflareaccess.com`;
const CERTS_URL = `${ISS}/cdn-cgi/access/certs`;
const KID = "test-key-1";

let signingKey: CryptoKey; // the key whose public half is published in the JWKS
let attackerKey: CryptoKey; // NOT in the JWKS — signatures must fail
let realFetch: typeof globalThis.fetch;

async function makeAssertion(
  key: CryptoKey,
  claims: { iss?: string; aud?: string; email?: string } = {},
): Promise<string> {
  return new SignJWT({ email: claims.email ?? "racer@example.com" })
    .setProtectedHeader({ alg: "RS256", kid: KID })
    .setIssuedAt()
    .setIssuer(claims.iss ?? ISS)
    .setAudience(claims.aud ?? AUD)
    .setExpirationTime("5m")
    .sign(key);
}

function requestWith(assertion: string): Request {
  return new Request("https://w/approve", {
    headers: { "Cf-Access-Jwt-Assertion": assertion },
  });
}

beforeAll(async () => {
  const pair = await generateKeyPair("RS256", { extractable: true });
  signingKey = pair.privateKey;
  const publicJwk = await exportJWK(pair.publicKey);
  publicJwk.kid = KID;
  publicJwk.alg = "RS256";
  publicJwk.use = "sig";

  attackerKey = (await generateKeyPair("RS256", { extractable: true })).privateKey;

  const jwksBody = JSON.stringify({ keys: [publicJwk] });
  realFetch = globalThis.fetch;
  globalThis.fetch = vi.fn(async (input: RequestInfo | URL) => {
    const u = typeof input === "string" ? input : input instanceof URL ? input.toString() : input.url;
    if (u === CERTS_URL) {
      return new Response(jwksBody, { headers: { "Content-Type": "application/json" } });
    }
    return realFetch(input as any);
  }) as unknown as typeof globalThis.fetch;
});

afterAll(() => {
  globalThis.fetch = realFetch;
});

describe("verifyAccessJwt", () => {
  it("accepts a valid assertion signed by the JWKS key", async () => {
    const assertion = await makeAssertion(signingKey, { email: "valid@example.com" });
    const identity = await verifyAccessJwt(requestWith(assertion), testEnv);
    expect(identity?.email).toBe("valid@example.com");
  });

  it("returns null when there is no assertion header", async () => {
    const identity = await verifyAccessJwt(new Request("https://w/approve"), testEnv);
    expect(identity).toBeNull();
  });

  it("rejects an assertion with the wrong audience", async () => {
    const assertion = await makeAssertion(signingKey, { aud: "some-other-app" });
    const identity = await verifyAccessJwt(requestWith(assertion), testEnv);
    expect(identity).toBeNull();
  });

  it("rejects an assertion with the wrong issuer", async () => {
    const assertion = await makeAssertion(signingKey, {
      iss: "https://evil.cloudflareaccess.com",
    });
    const identity = await verifyAccessJwt(requestWith(assertion), testEnv);
    expect(identity).toBeNull();
  });

  it("rejects an assertion signed by a key not in the JWKS", async () => {
    const assertion = await makeAssertion(attackerKey);
    const identity = await verifyAccessJwt(requestWith(assertion), testEnv);
    expect(identity).toBeNull();
  });
});

describe("resolveAccessEmail — ACCESS_DEV_BYPASS two-key gate", () => {
  it("honors the dev bypass when both ACCESS_DEV_BYPASS and ENVIRONMENT=development are set", async () => {
    const devEnv = { ...testEnv, ACCESS_DEV_BYPASS: "1", ENVIRONMENT: "development" };
    const req = new Request("https://w/approve", {
      headers: { "X-Dev-Access-Email": "dev@example.com" },
    });
    const email = await resolveAccessEmail(req, devEnv);
    expect(email).toBe("dev@example.com");
  });

  it("refuses the bypass when ACCESS_DEV_BYPASS is set but ENVIRONMENT is not 'development'", async () => {
    const misconfigured = { ...testEnv, ACCESS_DEV_BYPASS: "1", ENVIRONMENT: undefined };
    const req = new Request("https://w/approve", {
      headers: { "X-Dev-Access-Email": "dev@example.com" },
    });
    const email = await resolveAccessEmail(req, misconfigured);
    expect(email).toBeNull();
  });

  it("refuses the bypass when ENVIRONMENT=development is set but ACCESS_DEV_BYPASS is not", async () => {
    const misconfigured = { ...testEnv, ACCESS_DEV_BYPASS: undefined, ENVIRONMENT: "development" };
    const req = new Request("https://w/approve", {
      headers: { "X-Dev-Access-Email": "dev@example.com" },
    });
    const email = await resolveAccessEmail(req, misconfigured);
    expect(email).toBeNull();
  });

  it("returns null with no Access JWT and no bypass configured at all", async () => {
    const req = new Request("https://w/approve");
    const email = await resolveAccessEmail(req, testEnv);
    expect(email).toBeNull();
  });
});
