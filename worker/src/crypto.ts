// Small crypto helpers built on the Workers SubtleCrypto runtime.

const hex = (buf: ArrayBuffer): string =>
  Array.from(new Uint8Array(buf))
    .map((b) => b.toString(16).padStart(2, "0"))
    .join("");

export async function sha256Hex(input: string | ArrayBuffer): Promise<string> {
  const data =
    typeof input === "string" ? new TextEncoder().encode(input) : new Uint8Array(input);
  const digest = await crypto.subtle.digest("SHA-256", data);
  return hex(digest);
}

// URL-safe base64 (no padding) of `byteLength` cryptographically-random bytes.
// Encodes the FULL bit-stream (every bit of every byte), so 16 bytes yields the
// full 128 bits of entropy (22 base64url chars). A previous implementation kept
// only the low 6 bits of each byte, silently discarding a quarter of the entropy
// (16 bytes → 96 bits).
export function randomToken(byteLength = 16): string {
  const bytes = new Uint8Array(byteLength);
  crypto.getRandomValues(bytes);
  let binary = "";
  for (const b of bytes) binary += String.fromCharCode(b);
  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

// Crockford base32 (no I, L, O, U) — unambiguous, human-typable user codes.
const CROCKFORD = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

export function humanCode(length = 8): string {
  const bytes = new Uint8Array(length);
  crypto.getRandomValues(bytes);
  let out = "";
  for (const b of bytes) out += CROCKFORD[b & 0x1f];
  return out;
}

// Random UUID for primary keys.
export function newId(): string {
  return crypto.randomUUID();
}

// Constant-time string comparison (for CSRF tokens and similar secrets — avoids
// leaking match length/prefix via timing).
export function timingSafeEqualStr(a: string, b: string): boolean {
  if (a.length !== b.length) return false;
  let diff = 0;
  for (let i = 0; i < a.length; i++) diff |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return diff === 0;
}
