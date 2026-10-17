import { check } from 'k6';
import http from 'k6/http';

const baseUrl = __ENV.QUOTA_API_URL;
const rate = Number(__ENV.NEIGHBOR_RPS ?? 25);
const duration = __ENV.NEIGHBOR_DURATION ?? '150s';

export const options = {
  discardResponseBodies: true,
  scenarios: {
    neighbor: {
      executor: 'constant-arrival-rate',
      rate,
      timeUnit: '1s',
      duration,
      preAllocatedVUs: 50,
      maxVUs: 200,
    },
  },
};

export default function () {
  const res = http.get(`${baseUrl}/items/${Math.floor(Math.random() * 1000)}`, {
    headers: { 'X-Client': 'neighbor' },
    responseCallback: http.expectedStatuses(200, 429),
  });
  check(res, { 'accepted (200)': (r) => r.status === 200 });
}
