#!/usr/bin/env node
// Creates (or verifies) the playwright-tests Grafana user.
// Run once: pnpm test:pw:setup
// Requires in .env: GRAFANA_ADMIN_PASSWORD, PLAYWRIGHT_SERVICE_ACCOUNT_PW

const GRAFANA   = 'http://localhost:3000';
const ADMIN_USER = process.env.GRAFANA_ADMIN_USER ?? 'admin';
const ADMIN_PASS = process.env.GRAFANA_ADMIN_PASSWORD;
const PW_PASS    = process.env.PLAYWRIGHT_SERVICE_ACCOUNT_PW;

if (!ADMIN_PASS) { console.error('❌  GRAFANA_ADMIN_PASSWORD not set'); process.exit(1); }
if (!PW_PASS)    { console.error('❌  PLAYWRIGHT_SERVICE_ACCOUNT_PW not set'); process.exit(1); }

const auth = 'Basic ' + Buffer.from(`${ADMIN_USER}:${ADMIN_PASS}`).toString('base64');

async function api(method, path, body) {
  const res = await fetch(`${GRAFANA}${path}`, {
    method,
    headers: { 'Content-Type': 'application/json', Authorization: auth },
    body: body ? JSON.stringify(body) : undefined,
  });
  return { status: res.status, body: await res.json() };
}

const existing = await api('GET', '/api/users/lookup?loginOrEmail=playwright-tests');
if (existing.status === 200) {
  console.log('✅  playwright-tests already exists — updating password');
  await api('PUT', `/api/admin/users/${existing.body.id}/password`, { password: PW_PASS });
  console.log('✅  Done');
  process.exit(0);
}

const created = await api('POST', '/api/admin/users', {
  name: 'Playwright Tests',
  login: 'playwright-tests',
  email: 'playwright-tests@localhost',
  password: PW_PASS,
  role: 'Viewer',
});

if (created.status === 200) {
  console.log('✅  playwright-tests user created (Viewer)');
} else {
  console.error('❌  Failed:', created.body);
  process.exit(1);
}
