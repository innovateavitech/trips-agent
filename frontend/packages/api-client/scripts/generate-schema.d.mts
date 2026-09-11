/** Types for generate-schema.mjs, so the freshness test can import it under `tsc`. */
export declare function renderSchema(): Promise<string>;
export declare function readCommittedSchema(): Promise<string | null>;
