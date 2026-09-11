import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, sessionTransport } from '../../api/client';
import { API_BASE_URL } from '../../api/config';
import { ApiError, unwrap } from '../../api/errors';
import type { AgencyStatus, KybStatus, SubmissionStatus, UploadLimits } from './types';

export const kybKeys = {
  all: ['kyb'] as const,
  status: () => [...kybKeys.all, 'status'] as const,
  limits: () => [...kybKeys.all, 'limits'] as const,
};

/** While Trips is reviewing, the answer arrives without anyone pressing anything. */
const WHILE_UNDER_REVIEW = 15_000;

export function useKybStatus() {
  return useQuery({
    queryKey: kybKeys.status(),
    queryFn: async (): Promise<KybStatus> => {
      const raw = await unwrap(api.GET('/api/v1/kyb/status'));

      return {
        submissionId: raw.submissionId ?? null,
        status: (raw.status ?? 'Draft') as SubmissionStatus,
        agencyStatus: (raw.agencyStatus ?? 'PendingVerification') as AgencyStatus,
        submittedAt: raw.submittedAt ?? null,
        reviewedAt: raw.reviewedAt ?? null,
        rejectionReason: raw.rejectionReason ?? null,
        canEdit: raw.canEdit ?? false,
        documents: (raw.documents ?? []).map((d) => ({
          id: d.id ?? '',
          documentType: d.documentType ?? '',
          fileName: d.fileName ?? '',
          contentType: d.contentType ?? '',
          sizeBytes: Number(d.sizeBytes ?? 0),
          uploadedAt: d.uploadedAt ?? '',
        })),
        missingDocumentTypes: [...(raw.missingDocumentTypes ?? [])],
        canFundWallet: raw.canFundWallet ?? false,
        walletFundingBlockedReason: raw.walletFundingBlockedReason ?? null,
      };
    },
    refetchInterval: (query) => {
      const status = query.state.data?.status;
      return status === 'Submitted' || status === 'UnderReview' ? WHILE_UNDER_REVIEW : false;
    },
  });
}

export function useUploadLimits() {
  return useQuery({
    queryKey: kybKeys.limits(),
    queryFn: async (): Promise<UploadLimits> => {
      const raw = await unwrap(api.GET('/api/v1/kyb/limits'));

      return {
        maxSizeBytes: Number(raw.maxSizeBytes ?? 0),
        allowedContentTypes: [...(raw.allowedContentTypes ?? [])],
        allowedExtensions: [...(raw.allowedExtensions ?? [])],
        requiredDocumentTypes: [...(raw.requiredDocumentTypes ?? [])],
      };
    },
    // The limits change when the server changes, which is not during a session.
    staleTime: Infinity,
  });
}

/**
 * Sends one document, as multipart form data.
 *
 * Written against `authFetch` rather than the generated client: this is the one
 * endpoint that takes a file, and a typed JSON client has nothing to add to a
 * `FormData` body. Authentication and token refresh still come from the
 * transport, so it behaves like every other request.
 */
export function useUploadDocument() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: async ({ documentType, file }: { documentType: string; file: File }) => {
      const form = new FormData();
      form.append('documentType', documentType);
      form.append('file', file);

      const response = await sessionTransport.authFetch(
        new Request(`${API_BASE_URL}/api/v1/kyb/documents`, { method: 'POST', body: form }),
      );

      if (!response.ok) {
        throw ApiError.from(response, await bodyOf(response));
      }
    },
    // Uploading changes what is missing, and therefore whether submitting is
    // possible — so the whole step is re-read rather than patched locally.
    onSuccess: () => queryClient.invalidateQueries({ queryKey: kybKeys.all }),
  });
}

export function useSubmitKyb() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: () => unwrap(api.POST('/api/v1/kyb/submit')),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: kybKeys.all }),
  });
}

async function bodyOf(response: Response): Promise<unknown> {
  try {
    return await response.json();
  } catch {
    return null;
  }
}
