// Flight search under load (issue 109). Run through run.sh, which starts everything this needs;
// README.md says what the numbers mean.
//
//   MODE=cold  every request asks something no one has asked before, so every one misses the
//              net-rate cache and reaches the supplier stub.
//   MODE=warm  a fixed set of popular searches is run once in setup(), then the load repeats
//              them, so the cache answers.
//
// Two scenarios run one after the other, and are reported apart:
//   peak    the expected busy-hour rate, held (PEAK_RPS, default 20 a second)
//   stress  twice that, briefly, to show how much room is left (STRESS_RPS, default 40)

import http from 'k6/http';
import exec from 'k6/execution';
import { check, fail } from 'k6';
import { Rate, Counter } from 'k6/metrics';

const BASE_URL = __ENV.BASE_URL || 'http://127.0.0.1:5090';
const MODE = __ENV.MODE || 'cold';
const PEAK_RPS = Number(__ENV.PEAK_RPS || 20);
const STRESS_RPS = Number(__ENV.STRESS_RPS || 40);
const PEAK_DURATION = __ENV.PEAK_DURATION || '3m';
const STRESS_DURATION = __ENV.STRESS_DURATION || '1m';
const EMAIL = __ENV.LOADTEST_EMAIL || 'owner@lagostravel.example.com';
const PASSWORD = __ENV.LOADTEST_PASSWORD || 'Password123';

if (MODE !== 'cold' && MODE !== 'warm') {
  fail(`MODE must be cold or warm, not ${MODE}`);
}

// What the response said about the cache: the API's own fromCache flag, counted per request.
const servedFromCache = new Rate('search_served_from_cache');
const searchErrors = new Rate('search_errors');
const rateLimited = new Counter('search_rate_limited');

// Four domestic routes and two international: Nigerian agents sell mostly domestic, and the two
// endpoints behave differently at the supplier (one route per search, one answer per page).
const ROUTES = [
  ['LOS', 'ABV'],
  ['ABV', 'LOS'],
  ['LOS', 'PHC'],
  ['LOS', 'KAN'],
  ['LOS', 'LHR'],
  ['LOS', 'DXB'],
];

export const options = {
  discardResponseBodies: false,
  scenarios: {
    peak: {
      executor: 'constant-arrival-rate',
      rate: PEAK_RPS,
      timeUnit: '1s',
      duration: PEAK_DURATION,
      preAllocatedVUs: PEAK_RPS * 8,
      maxVUs: PEAK_RPS * 30,
    },
    stress: {
      executor: 'constant-arrival-rate',
      rate: STRESS_RPS,
      timeUnit: '1s',
      duration: STRESS_DURATION,
      startTime: PEAK_DURATION,
      preAllocatedVUs: STRESS_RPS * 8,
      maxVUs: STRESS_RPS * 30,
    },
  },
  // FRD section 2.3: 95% of searches inside 5 seconds. Recorded, not enforced as a gate — the
  // report states plainly whether each run met it (issue 109 asks exactly that).
  thresholds: {
    'http_req_duration{scenario:peak}': ['p(95)<5000'],
    'http_req_duration{scenario:stress}': ['p(95)<5000'],
    'search_errors{scenario:peak}': ['rate<0.01'],
    'search_errors{scenario:stress}': ['rate<0.01'],
  },
  summaryTrendStats: ['min', 'med', 'p(90)', 'p(95)', 'p(99)', 'max'],
  setupTimeout: '3m',
};

// A date from the first of next month onwards, so a long run never searches the past.
function dateAfterDays(days) {
  const now = new Date();
  const start = Date.UTC(now.getUTCFullYear(), now.getUTCMonth() + 1, 1);
  return new Date(start + days * 86400000).toISOString().slice(0, 10);
}

function searchBody(routeIndex, dayOffset, adults) {
  const [origin, destination] = ROUTES[routeIndex % ROUTES.length];
  return JSON.stringify({
    tripType: 'one_way',
    legs: [{ origin, destination, date: dateAfterDays(dayOffset) }],
    adults,
    children: 0,
    infants: 0,
    cabin: 'economy',
  });
}

// Cold: request n of the whole test gets a combination nobody has asked for. 6 routes x 330 days x
// 9 adult counts is 17,820 distinct searches — more than a run sends — so none repeats.
function coldBody(n) {
  return searchBody(n % 6, Math.floor(n / 6) % 330, 1 + (Math.floor(n / 1980) % 9));
}

// Warm: the popular searches — every route, the next five days, one or two adults. 60 in all.
const WARM_SET = [];
for (let route = 0; route < ROUTES.length; route++) {
  for (let day = 0; day < 5; day++) {
    for (let adults = 1; adults <= 2; adults++) {
      WARM_SET.push([route, day, adults]);
    }
  }
}

function headers(token) {
  return { headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token}` }, timeout: '60s' };
}

export function setup() {
  const login = http.post(`${BASE_URL}/api/v1/auth/login`, JSON.stringify({ email: EMAIL, password: PASSWORD }), {
    headers: { 'Content-Type': 'application/json' },
  });
  if (login.status !== 200) {
    fail(`sign-in failed with ${login.status}: ${login.body}`);
  }
  const token = login.json('accessToken');

  if (MODE === 'warm') {
    // Fill the cache, ten at a time, before the clock starts. Not counted in the results: these
    // run in setup, which k6 reports under its own tag.
    for (let i = 0; i < WARM_SET.length; i += 10) {
      const batch = WARM_SET.slice(i, i + 10).map(([route, day, adults]) => [
        'POST',
        `${BASE_URL}/api/v1/search/flights`,
        searchBody(route, day, adults),
        headers(token),
      ]);
      for (const res of http.batch(batch)) {
        if (res.status !== 200) {
          fail(`warming the cache failed with ${res.status}: ${res.body}`);
        }
      }
    }
  }

  return { token };
}

export default function (data) {
  let body;
  if (MODE === 'cold') {
    // iterationInTest counts from zero in each scenario, so stress starts past everything peak can
    // have sent (20 a second for three minutes is 3,600).
    const offset = exec.scenario.name === 'stress' ? 9000 : 0;
    body = coldBody((exec.scenario.iterationInTest + offset) % 17820);
  } else {
    const [route, day, adults] = WARM_SET[Math.floor(Math.random() * WARM_SET.length)];
    body = searchBody(route, day, adults);
  }

  const res = http.post(`${BASE_URL}/api/v1/search/flights`, body, headers(data.token));

  if (res.status === 429) {
    rateLimited.add(1);
  }

  const ok = check(res, {
    'status is 200': (r) => r.status === 200,
    'has offers': (r) => r.status === 200 && (r.json('offers') || []).length > 0,
  });
  searchErrors.add(!ok);

  if (res.status === 200) {
    servedFromCache.add(res.json('fromCache') === true);
  }
}

export function handleSummary(data) {
  const out = __ENV.SUMMARY_FILE || `results/${MODE}-summary.json`;
  return { [out]: JSON.stringify(data, null, 2), stdout: textSummary(data) };
}

function textSummary(data) {
  const lines = [`\nSearch load test, ${MODE} cache\n`];
  for (const scenario of ['peak', 'stress']) {
    const d = data.metrics[`http_req_duration{scenario:${scenario}}`];
    const e = data.metrics[`search_errors{scenario:${scenario}}`];
    if (!d) continue;
    const v = d.values;
    lines.push(
      `  ${scenario.padEnd(7)} p50 ${Math.round(v.med)} ms   p95 ${Math.round(v['p(95)'])} ms   ` +
        `p99 ${Math.round(v['p(99)'])} ms   max ${Math.round(v.max)} ms   errors ${e ? (e.values.rate * 100).toFixed(2) : '?'}%`,
    );
  }
  const c = data.metrics.search_served_from_cache;
  if (c) lines.push(`  served from cache: ${(c.values.rate * 100).toFixed(1)}%`);
  const reqs = data.metrics.http_reqs;
  if (reqs) lines.push(`  requests: ${reqs.values.count}`);
  return lines.join('\n') + '\n';
}
