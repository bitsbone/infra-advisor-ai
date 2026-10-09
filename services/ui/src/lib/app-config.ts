export interface AppConfig {
  env?: string;
}

declare global {
  interface Window {
    __APP_CONFIG__?: AppConfig;
  }
}

/**
 * Runtime environment name. Precedence: nginx-served /env-config.js (per pod),
 * then the build-time VITE_DD_ENV, then "dev".
 */
export function appEnv(): string {
  return window.__APP_CONFIG__?.env || (import.meta.env.VITE_DD_ENV as string | undefined) || "dev";
}

export function appVersion(): string {
  return (import.meta.env.VITE_APP_VERSION as string | undefined) || "local";
}
