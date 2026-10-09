// Local `vite dev` default. In containers nginx serves /env-config.js dynamically
// (exact-match location) with the pod's APP_ENV, which takes precedence over this file.
window.__APP_CONFIG__ = window.__APP_CONFIG__ || {};
