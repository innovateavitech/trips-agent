# Trips Africa API — Integration Notes (researched 2026-09-10)

Source: https://developer.tripsafrica.co/api-documentation/trips-api-airline-and-road-ticketing-vending-system
Index: https://developer.tripsafrica.co/llms.txt

## Scope of the API (CRITICAL)
Covers ONLY:
- International Flights (one-way, round-trip, multi-city)
- Domestic Flights (one-way, round-trip)
- Road Transport / Bus (one-way, round-trip)

NOT covered anywhere in the docs: hotels, visas, tours, payments, wallets,
webhooks/callbacks, OAuth/token endpoints.

## Auth
- Flights: static `Authorization: Bearer <token>`, `MerchantCode: ACCESS`, `Content-Type: application/json`
- Bus: `Authorization: Bearer SHA512(MerchantKey:MerchantCode)`, plus `MerchantKey` and `MerchantCode` headers
- No token refresh endpoint. Credentials are long-lived merchant secrets.

## Endpoints
Base: https://api.staging.trips.ng

| Endpoint | Method | Purpose |
|---|---|---|
| /api/Flight/SearchFlight | POST | Intl search (1-way / round / multi-city) |
| /api/Flight/ConfirmTicketPrice | POST | Intl price lock + PNR hold |
| /api/Flight/Domestic/SearchFlight | POST | Domestic search |
| /api/Flight/Domestic/ConfirmTicketPrice | POST | Domestic price lock (returns ARRAY) |
| /api/v2/ticketing/issue | POST | Issue ticket (all products) |
| /api/Flight/MyBookings | GET | Query by pnr / email / phone |
| /api/Flight/GetBookingStatus | POST | Re-query status (ConfirmationCode + Surname) |
| /api/Flight/FlightRules | POST | Fare rules + penalties |
| /api/Bus/SearchBus | POST | Bus search |
| /api/Bus/ConfirmTicketPrice | POST | Bus price lock (returns ARRAY) |
| /api/Bus/CancelTicketBooking | POST | Cancel bus booking |
| /api/Bus/MyReservation | POST | Bus reservation lookup |
| /api/Bus/TravelRules | POST | Bus fare rules |

## Flow
Search -> (SessionId, AgentId, GdsId, CombinationID, RecommendationID)
     -> ConfirmTicketPrice (returns ConfirmationCode, TicketTimeLimit, OldPrice/NewPrice, Hash)
     -> VALIDATE HASH  (see below)
     -> /api/v2/ticketing/issue { SessionId, TripType, TripMode }
     -> Pnr, IsSuccessful, BookingStatus

TripType: "International" | "Domestic"
TripMode: "Flight" | "Road"

## Hash validation (MUST be server-side)
SHA512( "{MerchantKey}*{ConfirmationCode}*{NewPriceWhole}" ) -> lowercase hex
Compare to response `Hash`. Domestic/round-trip confirm returns an ARRAY —
validate EACH entry independently against its own ConfirmationCode/NewPriceWhole.

## Booking status codes
| Code | Meaning |
|---|---|
| 0 | Booking |
| 1 | Cancelled |
| 2 | TicketIssued |
| 3 | TicketPending |
| 11 | CancellationFailed |
| 100 | Error |

## Payment reversal rules (business-critical)
- Reverse payment when HTTP 200 AND status in {0, 1, 11}
- Reverse payment when HTTP 400 AND GetBookingStatus returns status in {0, 1, 11}
- Implies: we MUST poll GetBookingStatus after issue; there are no webhooks.
- TicketPending (3) is NOT terminal -> requires a polling reconciliation worker.

## Architectural consequences
1. No webhooks => background poller for every booking in a non-terminal state.
2. TicketTimeLimit => scheduled expiry job per held booking.
3. Price can change between search and confirm (OldPrice vs NewPrice) =>
   re-confirm + customer re-consent step required.
4. Search is slow/paginated (PageSize 50) => cache search results by
   normalised criteria; store raw supplier payloads for replay/audit.
5. Merchant credentials appear to be platform-level, not per-agent —
   contradicts FRD "Agent's Trips Africa API credentials are active".
   Model credentials per-tenant-capable anyway (SupplierCredential table).
6. Issue is NOT idempotent per the docs => we must supply our own idempotency
   key + distributed lock to avoid double-ticketing.

## FRD contradictions found
- FRD 1.3 says hotels are OUT of scope, but FRD 2.3 UC-1B is a hotel booking
  use case. No hotel API exists at Trips Africa anyway.
- FRD never mentions Road/Bus ticketing, but the API supports it and it is
  highly relevant to the Nigerian market.
