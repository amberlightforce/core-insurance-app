/* eslint-disable react-refresh/only-export-components -- test helper module, never hot-reloaded */
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { RenderResult } from '@testing-library/react';
import type { ReactElement } from 'react';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router';
import { vi } from 'vitest';

import { renderWithDs } from './render';

export interface MockRequest {
  method: string;
  url: URL;
  body: unknown;
  headers: Headers;
}

export interface MockReply {
  status?: number;
  body?: unknown;
}

export interface MockRoute {
  method: string;
  /** Matched against the URL path (without query string). */
  path: string | RegExp;
  respond: (request: MockRequest) => MockReply | Promise<MockReply>;
}

export interface MockApi {
  calls: MockRequest[];
  /** Calls whose path matches. */
  callsTo: (method: string, path: string | RegExp) => MockRequest[];
}

const matches = (pattern: string | RegExp, path: string) =>
  typeof pattern === 'string' ? pattern === path : pattern.test(path);

/** Stubs `fetch` with in-memory handlers (the contract mocks of the staff screens). Unmatched calls fail the test. */
export function mockApi(routes: MockRoute[]): MockApi {
  const calls: MockRequest[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = new URL(
        typeof input === 'string' ? input : input instanceof URL ? input.href : input.url,
        'http://localhost',
      );
      const method = (init?.method ?? 'GET').toUpperCase();
      const raw = typeof init?.body === 'string' ? init.body : undefined;
      const request: MockRequest = {
        method,
        url,
        body: raw ? (JSON.parse(raw) as unknown) : undefined,
        headers: new Headers(init?.headers),
      };
      calls.push(request);
      const route = routes.find((r) => r.method === method && matches(r.path, url.pathname));
      if (!route) throw new Error(`Unmocked API call: ${method} ${url.pathname}${url.search}`);
      const reply = await route.respond(request);
      const status = reply.status ?? 200;
      return new Response(reply.body === undefined ? null : JSON.stringify(reply.body), {
        status,
        headers: {
          'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json',
        },
      });
    }),
  );
  return {
    calls,
    callsTo: (method, path) =>
      calls.filter((c) => c.method === method && matches(path, c.url.pathname)),
  };
}

export function problem(status: number, code: string, title: string, detail?: string) {
  return {
    status,
    body: {
      type: `/problems/${code}`,
      title,
      status,
      ...(detail ? { detail } : {}),
      code,
      traceId: 'trace-0123456789abcdef',
      retryable: false,
    },
  };
}

function LocationProbe() {
  const location = useLocation();
  return <output data-testid="location">{location.pathname + location.search}</output>;
}

/** Renders a screen inside the design system, a fresh QueryClient and a MemoryRouter at `url` for route `path`. */
export function renderScreen(
  element: ReactElement,
  { path, url }: { path: string; url: string },
): RenderResult & { user: ReturnType<typeof renderWithDs>['user'] } {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } });
  return renderWithDs(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={[url]}>
        <Routes>
          <Route path={path} element={element} />
        </Routes>
        <LocationProbe />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}
