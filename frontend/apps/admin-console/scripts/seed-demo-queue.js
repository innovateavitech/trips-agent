/* global process */

/**
 * Puts agencies in the KYB review queue, so the console has something real to show.
 *
 *   cd frontend && pnpm --filter admin-console demo:seed
 *
 * It does exactly what a travel business does, through the same public endpoints: register, verify
 * the email, upload documents, submit. Nothing is written to the database directly, so what the
 * reviewer sees is what the real flow produces — including the file sizes, the checksums and the
 * `admin_alerts` row the submission raises.
 *
 * It needs the API on :5002 and Mailpit on :8025 (docker compose up -d), and it only ever talks to
 * localhost. Running it twice adds three more agencies; the seeded "Pending Travel" account is
 * only submitted once.
 *
 * The password below is IdentitySeedData.DevelopmentPassword — a published constant that exists
 * so a fresh clone can sign in. It is not a secret, and seeding only runs against a local database.
 */

import { deflateSync } from 'node:zlib';

const API = process.env.TRIPS_API_URL ?? 'http://localhost:5002';
const MAILPIT = process.env.MAILPIT_URL ?? 'http://localhost:8025';
const PASSWORD = 'Password123';

/** Seeded by DatabaseSeeder, and deliberately left unverified. It becomes the oldest in the queue. */
const SEEDED_PENDING_OWNER = 'owner@pendingtravel.test';

const DEMO_AGENCIES = [
  {
    businessName: 'Harmattan Holidays',
    firstName: 'Tunde',
    lastName: 'Bakare',
    domain: 'harmattanholidays.test',
  },
  {
    businessName: 'Eko Wings Travel',
    firstName: 'Amaka',
    lastName: 'Obi',
    domain: 'ekowings.test',
  },
  {
    businessName: 'Sahel Routes Tours',
    firstName: 'Musa',
    lastName: 'Ibrahim',
    domain: 'sahelroutes.test',
  },
];

async function main() {
  await checkReachable();

  out('Filling the KYB review queue.\n');

  await putInQueue('Pending Travel', SEEDED_PENDING_OWNER, { alreadyRegistered: true });

  // A suffix per run, so a second run adds new agencies instead of colliding with its own.
  const run = Date.now().toString(36).slice(-5);
  for (const agency of DEMO_AGENCIES) {
    const email = `owner.${run}@${agency.domain}`;
    await register(agency, email);
    await putInQueue(agency.businessName, email);
  }

  out('\nDone. Sign in at http://localhost:5174 as ops@tripsagent.test');
}

/** Registers a business, then verifies the address with the code Mailpit caught. */
async function register(agency, email) {
  const sentAfter = Date.now() - 2000;

  const response = await api('POST', '/api/v1/auth/register', {
    body: {
      businessName: agency.businessName,
      firstName: agency.firstName,
      lastName: agency.lastName,
      email,
      phoneNumber: null,
      countryCode: 'NG',
      password: PASSWORD,
    },
  });

  if (response.status !== 202) {
    throw new Error(`Could not register ${agency.businessName} (HTTP ${response.status}).`);
  }

  await verifyEmail(email, sentAfter);
}

async function verifyEmail(email, sentAfter) {
  const code = await waitForCode(email, sentAfter);
  const response = await api('POST', '/api/v1/auth/verify-email', { body: { email, code } });

  if (!response.ok) {
    throw new Error(`The verification code for ${email} was refused (HTTP ${response.status}).`);
  }
}

/** Uploads whatever documents are still missing, then submits. */
async function putInQueue(label, email, { alreadyRegistered = false } = {}) {
  let token = await signIn(email);

  if (token === null && alreadyRegistered) {
    // The seeded owner has never confirmed their address, which is the state the onboarding
    // screens are built for. Ask for a fresh code and use it.
    const sentAfter = Date.now() - 2000;
    await api('POST', '/api/v1/auth/resend-verification', { body: { email } });
    await verifyEmail(email, sentAfter);
    token = await signIn(email);
  }

  if (token === null) throw new Error(`Could not sign in as ${email}.`);

  const status = (await api('GET', '/api/v1/kyb/status', { token })).payload;

  if (!status.canEdit) {
    out(`  ${label}: already ${status.status.toLowerCase()} — left alone`);
    return;
  }

  const wanted = ['CertificateOfIncorporation', 'TaxIdentification', 'ProofOfAddress'];
  const missing = wanted.filter(
    (type) =>
      status.missingDocumentTypes.includes(type) ||
      !status.documents.some((document) => document.documentType === type),
  );

  for (const type of missing) {
    const file = documentFor(type, label);
    await upload(token, type, file);
  }

  const submitted = await api('POST', '/api/v1/kyb/submit', { token });
  if (!submitted.ok) {
    throw new Error(`${label} could not submit (HTTP ${submitted.status}).`);
  }

  out(`  ${label}: submitted with ${missing.length} document(s)`);
}

async function upload(token, documentType, file) {
  const form = new FormData();
  form.append('documentType', documentType);
  form.append('file', new Blob([file.bytes], { type: file.contentType }), file.fileName);

  const response = await api('POST', '/api/v1/kyb/documents', { token, form });
  if (!response.ok) {
    throw new Error(
      `Upload of ${file.fileName} failed (HTTP ${response.status}): ${response.payload?.title ?? ''}`,
    );
  }
}

/** The access token, or null when the address is not verified yet. */
async function signIn(email) {
  const response = await api('POST', '/api/v1/auth/login', {
    body: { email, password: PASSWORD },
  });

  if (response.ok) return response.payload.accessToken;
  if (response.status === 403) return null;
  throw new Error(`Signing in as ${email} failed (HTTP ${response.status}).`);
}

/**
 * The verification code, read out of Mailpit's subject line ("123456 is your … code").
 *
 * Mailpit catches every email the API sends locally, so this is how a script joins a flow that
 * was designed around a human reading their inbox.
 */
async function waitForCode(email, sentAfter) {
  const deadline = Date.now() + 20_000;

  while (Date.now() < deadline) {
    const response = await fetch(
      `${MAILPIT}/api/v1/search?query=${encodeURIComponent(`to:${email}`)}`,
    );

    if (response.ok) {
      const { messages = [] } = await response.json();

      for (const message of messages) {
        const code = /^(\d{4,8}) is your/.exec(message.Subject ?? '')?.[1];
        const arrived = Date.parse(message.Created ?? '');
        if (code && (Number.isNaN(arrived) || arrived >= sentAfter)) return code;
      }
    }

    await sleep(500);
  }

  throw new Error(
    `No verification code arrived for ${email}. Is Mailpit running, and is the API using it for SMTP?`,
  );
}

async function api(method, path, { token, body, form } = {}) {
  const headers = { Accept: 'application/json' };
  if (token) headers.Authorization = `Bearer ${token}`;
  if (body !== undefined) headers['Content-Type'] = 'application/json';

  const response = await fetch(`${API}${path}`, {
    method,
    headers,
    body: form ?? (body === undefined ? undefined : JSON.stringify(body)),
  });

  const text = await response.text();
  let payload = null;
  try {
    payload = text ? JSON.parse(text) : null;
  } catch {
    payload = null;
  }

  return { ok: response.ok, status: response.status, payload };
}

async function checkReachable() {
  try {
    await fetch(`${API}/health`);
  } catch {
    throw new Error(
      `The API is not answering on ${API}. Start it with:\n` +
        '  cd backend && dotnet run --project services/TripsAgent.Api',
    );
  }
}

// ---------------------------------------------------------------------------
//  Stand-in documents
//
//  Real files, not empty ones: the API sniffs the leading bytes and refuses anything that is not
//  genuinely a PDF, JPEG or PNG (FileSignature in the domain), so a file of zeroes would be
//  rejected exactly as a renamed .exe would be.
// ---------------------------------------------------------------------------

function documentFor(documentType, businessName) {
  if (documentType === 'ProofOfAddress') {
    return {
      fileName: 'utility-bill.png',
      contentType: 'image/png',
      bytes: scanLikePng(640, 440),
    };
  }

  const titles = {
    CertificateOfIncorporation: 'Certificate of Incorporation',
    TaxIdentification: 'Tax Identification Certificate',
  };
  const title = titles[documentType] ?? 'Supporting Document';

  return {
    fileName: `${documentType === 'TaxIdentification' ? 'tax-identification' : 'certificate-of-incorporation'}.pdf`,
    contentType: 'application/pdf',
    bytes: simplePdf(title, [
      businessName,
      'Corporate Affairs Commission, Federal Republic of Nigeria',
      '',
      'RC 1048221',
      'Registered 14 March 2019',
      '',
      'DEMONSTRATION DOCUMENT - not a real registration.',
    ]),
  };
}

/** A one-page PDF with a title and some lines, built by hand so there is no dependency. */
function simplePdf(title, lines) {
  const escape = (text) => text.replace(/([\\()])/g, '\\$1');

  const content = [
    'BT',
    '/F1 20 Tf',
    '72 720 Td',
    `(${escape(title)}) Tj`,
    '/F1 11 Tf',
    ...lines.flatMap((line) => ['0 -26 Td', `(${escape(line)}) Tj`]),
    'ET',
  ].join('\n');

  const objects = [
    '<< /Type /Catalog /Pages 2 0 R >>',
    '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
    '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>',
    '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>',
    `<< /Length ${content.length} >>\nstream\n${content}\nendstream`,
  ];

  let pdf = '%PDF-1.4\n';
  const offsets = [];
  objects.forEach((body, index) => {
    offsets.push(pdf.length);
    pdf += `${index + 1} 0 obj\n${body}\nendobj\n`;
  });

  const startXref = pdf.length;
  pdf +=
    `xref\n0 ${objects.length + 1}\n0000000000 65535 f \n` +
    offsets.map((offset) => `${String(offset).padStart(10, '0')} 00000 n \n`).join('') +
    `trailer\n<< /Size ${objects.length + 1} /Root 1 0 R >>\nstartxref\n${startXref}\n%%EOF\n`;

  // The file is ASCII throughout, so one character is one byte and the offsets above are right.
  return new TextEncoder().encode(pdf);
}

/**
 * A greyscale PNG that reads as a scanned bill: a frame, a heading block and some text lines.
 * Greyscale (colour type 0) keeps it to one channel and out of the design system's way.
 */
function scanLikePng(width, height) {
  const shade = (x, y) => {
    if (x < 6 || y < 6 || x >= width - 6 || y >= height - 6) return 90;
    if (y > 40 && y < 72 && x > 40 && x < width * 0.55) return 120;
    if (y > 120 && (y - 120) % 34 < 12 && x > 40 && x < width * 0.82) return 175;
    return 248;
  };

  const raw = new Uint8Array((width + 1) * height);
  for (let y = 0; y < height; y += 1) {
    raw[y * (width + 1)] = 0; // no per-row filter
    for (let x = 0; x < width; x += 1) raw[y * (width + 1) + 1 + x] = shade(x, y);
  }

  const header = new Uint8Array([...be32(width), ...be32(height), 8, 0, 0, 0, 0]);

  return concat([
    new Uint8Array([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', header),
    chunk('IDAT', new Uint8Array(deflateSync(raw))),
    chunk('IEND', new Uint8Array()),
  ]);
}

function chunk(type, data) {
  const typeBytes = new TextEncoder().encode(type);
  const body = concat([typeBytes, data]);
  return concat([new Uint8Array(be32(data.length)), body, new Uint8Array(be32(crc32(body)))]);
}

function be32(value) {
  return [(value >>> 24) & 0xff, (value >>> 16) & 0xff, (value >>> 8) & 0xff, value & 0xff];
}

function crc32(bytes) {
  let crc = 0xffffffff;
  for (const byte of bytes) {
    crc ^= byte;
    for (let bit = 0; bit < 8; bit += 1) {
      crc = (crc >>> 1) ^ (0xedb88320 & -(crc & 1));
    }
  }
  return (crc ^ 0xffffffff) >>> 0;
}

function concat(parts) {
  const total = parts.reduce((sum, part) => sum + part.length, 0);
  const result = new Uint8Array(total);
  let offset = 0;
  for (const part of parts) {
    result.set(part, offset);
    offset += part.length;
  }
  return result;
}

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function out(line) {
  process.stdout.write(`${line}\n`);
}

try {
  await main();
} catch (error) {
  process.stderr.write(`\n${error.message}\n`);
  process.exitCode = 1;
}
