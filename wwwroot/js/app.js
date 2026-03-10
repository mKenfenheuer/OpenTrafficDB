/**
 * OpenTrafficDB – shared app utilities (cookie-based auth)
 */

const API_BASE = '/api/v1';
const WEB_BASE = '/web';
const AUTH_BASE = '/auth';

// ── Fetch wrappers ─────────────────────────────────────────────────────────────

/** Fetch a web (cookie-authenticated) endpoint. Redirects to / on 401. */
export async function web(method, path, body = null) {
  const res = await fetch(WEB_BASE + path, {
    method,
    headers: body ? { 'Content-Type': 'application/json' } : {},
    body: body ? JSON.stringify(body) : undefined,
    credentials: 'same-origin'
  });
  if (res.status === 401) { window.location.href = '/'; return null; }
  const data = await res.json().catch(() => ({}));
  return { ok: res.ok, status: res.status, data };
}

/** Fetch a public /auth endpoint (register, login, logout). */
export async function auth(method, path, body = null) {
  const res = await fetch(AUTH_BASE + path, {
    method,
    headers: body ? { 'Content-Type': 'application/json' } : {},
    body: body ? JSON.stringify(body) : undefined,
    credentials: 'same-origin'
  });
  const data = await res.json().catch(() => ({}));
  return { ok: res.ok, status: res.status, data };
}

// ── Session ────────────────────────────────────────────────────────────────────

/** Fetch the current user's profile from the cookie session.
 *  Returns the profile object or null if not logged in. */
export async function getSession() {
  const res = await fetch(WEB_BASE + '/me', { credentials: 'same-origin' });
  if (!res.ok) return null;
  return res.json().catch(() => null);
}

// ── Logout ─────────────────────────────────────────────────────────────────────

export async function logout() {
  await fetch(AUTH_BASE + '/logout', { method: 'POST', credentials: 'same-origin' });
  window.location.href = '/';
}
window.logout = logout;

// ── Navbar ─────────────────────────────────────────────────────────────────────

/** Render the navbar. Pass the session object (or null) returned by getSession(). */
export function initNavbar(session, activeLink = '') {
  const nav = document.getElementById('navbar-links');
  if (!nav) return;

  let links = `<li><a href="/swagger" target="_blank">API Docs</a></li>`;

  if (session) {
    links += `<li><a href="/dashboard.html" ${activeLink === 'dashboard' ? 'class="active"' : ''}>Dashboard</a></li>`;
    links += `<li><a href="/profile.html" ${activeLink === 'profile' ? 'class="active"' : ''}>Profile</a></li>`;
    if (session.role === 'Admin')
      links += `<li><a href="/admin.html" ${activeLink === 'admin' ? 'class="active"' : ''}>Admin</a></li>`;
    const displayName = session.name || session.email || 'Account';
    links += `<li><span class="text-muted text-sm" style="padding:6px 8px">${esc(displayName)}</span></li>`;
    links += `<li><button class="btn btn-secondary btn-sm" onclick="logout()">Sign out</button></li>`;
  } else {
    links += `<li><a href="/register.html" ${activeLink === 'register' ? 'class="active"' : ''}>Register</a></li>`;
    links += `<li><a href="/" class="btn btn-primary btn-sm" style="margin-left:4px">Sign in</a></li>`;
  }

  nav.innerHTML = links;
}

// ── UI helpers ─────────────────────────────────────────────────────────────────

export function roleBadge(role) {
  const map = {
    User:        '<span class="badge badge-user">User</span>',
    Contributor: '<span class="badge badge-contributor">Contributor</span>',
    Admin:       '<span class="badge badge-admin">Admin</span>',
  };
  return map[role] ?? `<span class="badge">${esc(role)}</span>`;
}

export function fmtDate(iso) {
  if (!iso) return '—';
  return new Date(iso).toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' });
}

export function fmtDateTime(iso) {
  if (!iso) return '—';
  return new Date(iso).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });
}

export function showAlert(el, message, type = 'error') {
  el.className = `alert alert-${type}`;
  el.textContent = message;
  el.classList.remove('hidden');
}

export function hideAlert(el) { el.classList.add('hidden'); }

export function copyToClipboard(text, btn) {
  navigator.clipboard.writeText(text).then(() => {
    const orig = btn.textContent;
    btn.textContent = 'Copied!';
    btn.classList.add('copied');
    setTimeout(() => { btn.textContent = orig; btn.classList.remove('copied'); }, 2000);
  });
}

/** Escape HTML to prevent XSS when inserting user data into innerHTML. */
export function esc(str) {
  return String(str ?? '')
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}
