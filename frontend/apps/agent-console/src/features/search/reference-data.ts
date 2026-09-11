import type { Airport, BusTerminal } from './types';

/**
 * ============================================================================
 *  TEMPORARY reference data, until the supplier's own lists are seeded.
 * ============================================================================
 *
 * Airports will come from the supplier's airport list, and bus terminals from
 * the terminal IDs #34 sources and seeds — the ids below are placeholders, not
 * the supplier's. The names are real, so the screens read true in a demo.
 *
 * Nigerian airports first, busiest first: an empty airport field suggests the
 * top of this list, and nine searches in ten start at LOS or ABV.
 */
export const AIRPORTS: readonly Airport[] = [
  { code: 'LOS', city: 'Lagos', name: 'Murtala Muhammed International', country: 'NG' },
  { code: 'ABV', city: 'Abuja', name: 'Nnamdi Azikiwe International', country: 'NG' },
  { code: 'PHC', city: 'Port Harcourt', name: 'Port Harcourt International', country: 'NG' },
  { code: 'KAN', city: 'Kano', name: 'Mallam Aminu Kano International', country: 'NG' },
  { code: 'ENU', city: 'Enugu', name: 'Akanu Ibiam International', country: 'NG' },
  { code: 'QOW', city: 'Owerri', name: 'Sam Mbakwe International', country: 'NG' },
  { code: 'BNI', city: 'Benin City', name: 'Benin Airport', country: 'NG' },
  { code: 'QUO', city: 'Uyo', name: 'Victor Attah International', country: 'NG' },
  { code: 'CBQ', city: 'Calabar', name: 'Margaret Ekpo International', country: 'NG' },
  { code: 'ABB', city: 'Asaba', name: 'Asaba International', country: 'NG' },
  { code: 'QRW', city: 'Warri', name: 'Osubi Airport', country: 'NG' },
  { code: 'KAD', city: 'Kaduna', name: 'Kaduna International', country: 'NG' },
  { code: 'IBA', city: 'Ibadan', name: 'Ibadan Airport', country: 'NG' },
  { code: 'ILR', city: 'Ilorin', name: 'Ilorin International', country: 'NG' },
  { code: 'JOS', city: 'Jos', name: 'Yakubu Gowon Airport', country: 'NG' },
  { code: 'SKO', city: 'Sokoto', name: 'Sadiq Abubakar III International', country: 'NG' },
  { code: 'YOL', city: 'Yola', name: 'Yola Airport', country: 'NG' },
  { code: 'MIU', city: 'Maiduguri', name: 'Maiduguri International', country: 'NG' },
  { code: 'AKR', city: 'Akure', name: 'Akure Airport', country: 'NG' },
  { code: 'GMO', city: 'Gombe', name: 'Gombe Lawanti International', country: 'NG' },
  { code: 'ACC', city: 'Accra', name: 'Kotoka International', country: 'GH' },
  { code: 'LFW', city: 'Lomé', name: 'Gnassingbé Eyadéma International', country: 'TG' },
  { code: 'ABJ', city: 'Abidjan', name: 'Félix-Houphouët-Boigny International', country: 'CI' },
  { code: 'DSS', city: 'Dakar', name: 'Blaise Diagne International', country: 'SN' },
  { code: 'COO', city: 'Cotonou', name: 'Cadjehoun Airport', country: 'BJ' },
  { code: 'LHR', city: 'London', name: 'Heathrow', country: 'GB' },
  { code: 'LGW', city: 'London', name: 'Gatwick', country: 'GB' },
  { code: 'CDG', city: 'Paris', name: 'Charles de Gaulle', country: 'FR' },
  { code: 'AMS', city: 'Amsterdam', name: 'Schiphol', country: 'NL' },
  { code: 'IST', city: 'Istanbul', name: 'Istanbul Airport', country: 'TR' },
  { code: 'DXB', city: 'Dubai', name: 'Dubai International', country: 'AE' },
  { code: 'DOH', city: 'Doha', name: 'Hamad International', country: 'QA' },
  { code: 'ADD', city: 'Addis Ababa', name: 'Bole International', country: 'ET' },
  { code: 'NBO', city: 'Nairobi', name: 'Jomo Kenyatta International', country: 'KE' },
  { code: 'JNB', city: 'Johannesburg', name: 'O. R. Tambo International', country: 'ZA' },
  { code: 'CAI', city: 'Cairo', name: 'Cairo International', country: 'EG' },
  { code: 'JFK', city: 'New York', name: 'John F. Kennedy International', country: 'US' },
  { code: 'IAH', city: 'Houston', name: 'George Bush Intercontinental', country: 'US' },
  { code: 'ATL', city: 'Atlanta', name: 'Hartsfield-Jackson International', country: 'US' },
  { code: 'YYZ', city: 'Toronto', name: 'Pearson International', country: 'CA' },
];

export const BUS_TERMINALS: readonly BusTerminal[] = [
  { id: 'trm_lag_jibowu', city: 'Lagos', name: 'Jibowu' },
  { id: 'trm_lag_ojota', city: 'Lagos', name: 'Ojota' },
  { id: 'trm_lag_iyana_ipaja', city: 'Lagos', name: 'Iyana-Ipaja' },
  { id: 'trm_lag_ajah', city: 'Lagos', name: 'Ajah' },
  { id: 'trm_abj_utako', city: 'Abuja', name: 'Utako' },
  { id: 'trm_abj_jabi', city: 'Abuja', name: 'Jabi' },
  { id: 'trm_abj_kubwa', city: 'Abuja', name: 'Kubwa' },
  { id: 'trm_ibd_iwo_road', city: 'Ibadan', name: 'Iwo Road' },
  { id: 'trm_ibd_challenge', city: 'Ibadan', name: 'Challenge' },
  { id: 'trm_bni_ramat_park', city: 'Benin City', name: 'Ramat Park' },
  { id: 'trm_bni_uselu', city: 'Benin City', name: 'Uselu' },
  { id: 'trm_enu_holy_ghost', city: 'Enugu', name: 'Holy Ghost' },
  { id: 'trm_ons_upper_iweka', city: 'Onitsha', name: 'Upper Iweka' },
  { id: 'trm_owr_douglas_road', city: 'Owerri', name: 'Douglas Road' },
  { id: 'trm_phc_waterlines', city: 'Port Harcourt', name: 'Waterlines' },
  { id: 'trm_phc_rumuokoro', city: 'Port Harcourt', name: 'Rumuokoro' },
  { id: 'trm_asb_summit', city: 'Asaba', name: 'Summit Junction' },
  { id: 'trm_war_effurun', city: 'Warri', name: 'Effurun' },
  { id: 'trm_kad_kawo', city: 'Kaduna', name: 'Kawo' },
];

export function findAirport(code: string): Airport | undefined {
  return AIRPORTS.find((airport) => airport.code === code);
}

export function findTerminal(id: string): BusTerminal | undefined {
  return BUS_TERMINALS.find((terminal) => terminal.id === id);
}

/** Both ends in Nigeria. Domestic fares, carriers and baggage all differ from international. */
export function isDomestic(originCode: string, destinationCode: string): boolean {
  return findAirport(originCode)?.country === 'NG' && findAirport(destinationCode)?.country === 'NG';
}

/** Terminals grouped by city, in list order — for an `<optgroup>` per city. */
export function terminalsByCity(): Array<{ city: string; terminals: BusTerminal[] }> {
  const groups: Array<{ city: string; terminals: BusTerminal[] }> = [];
  for (const terminal of BUS_TERMINALS) {
    const group = groups.find((candidate) => candidate.city === terminal.city);
    if (group) group.terminals.push(terminal);
    else groups.push({ city: terminal.city, terminals: [terminal] });
  }
  return groups;
}
