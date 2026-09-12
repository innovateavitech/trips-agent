import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { Schemas } from '@trips/api-client';
import { api } from '../../api/client';
import { unwrap } from '../../api/errors';

/**
 * Reading and changing the agency's website (issues 58 and 59).
 *
 * Everything sits under one key, because almost every change here touches more than one thing the
 * screens show: saving a page changes whether there is anything to stage, staging changes what can
 * be published, publishing changes the version history, and adding a hostname changes the site's
 * address. One invalidation after any of them keeps the whole section honest rather than leaving a
 * panel showing yesterday's answer.
 */

export const storefrontKeys = {
  all: ['storefront'] as const,
  templates: () => [...storefrontKeys.all, 'templates'] as const,
  site: () => [...storefrontKeys.all, 'site'] as const,
  theme: () => [...storefrontKeys.all, 'theme'] as const,
  versions: () => [...storefrontKeys.all, 'versions'] as const,
  domains: () => [...storefrontKeys.all, 'domains'] as const,
  page: (pageId: string) => [...storefrontKeys.all, 'page', pageId] as const,
};

export type SiteTemplate = Schemas['SiteTemplateResponse'];
export type Site = Schemas['SiteResponse'];
export type SiteSettings = Schemas['SiteSettingsResponse'];
export type SitePage = Schemas['SitePageResponse'];
export type SitePageSummary = Schemas['SitePageSummaryResponse'];
export type SiteBlock = Schemas['SiteBlockDto'];
export type SitePageBlock = Schemas['SitePageBlockResponse'];
export type SiteTheme = Schemas['SiteThemeResponse'];
export type SiteVersion = Schemas['SiteVersionResponse'];
export type SiteDomain = Schemas['SiteDomainResponse'];
export type SitePreviewLink = Schemas['SitePreviewLinkResponse'];

/** The templates a new site can be built from. They do not change during a session. */
export function useSiteTemplates() {
  return useQuery({
    queryKey: storefrontKeys.templates(),
    queryFn: () => unwrap(api.GET('/api/v1/storefront/templates')),
    staleTime: Infinity,
  });
}

/**
 * The agency's site, or null when they have not made one yet.
 *
 * A 404 here is an answer, not a failure: "you have no website yet" is the state the first screen is
 * built around, so it must not arrive as an error.
 */
export function useSite() {
  return useQuery({
    queryKey: storefrontKeys.site(),
    queryFn: async (): Promise<Site | null> => {
      const { data, error, response } = await api.GET('/api/v1/storefront/site');

      if (response.status === 404) {
        return null;
      }

      if (error || !data) {
        return unwrap(Promise.resolve({ data, error, response }));
      }

      return data;
    },
  });
}

export function useCreateSite() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (templateCode: string) =>
      unwrap(api.POST('/api/v1/storefront/site', { body: { templateCode } })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: storefrontKeys.all }),
  });
}

export function useSaveSiteSettings() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (body: Schemas['SiteSettingsRequest']) =>
      unwrap(api.PUT('/api/v1/storefront/site/settings', { body })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: storefrontKeys.all }),
  });
}

export function useSiteTheme() {
  return useQuery({
    queryKey: storefrontKeys.theme(),
    queryFn: () => unwrap(api.GET('/api/v1/storefront/theme')),
  });
}

export function useSaveSiteTheme() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (body: Schemas['SiteThemeRequest']) =>
      unwrap(api.PUT('/api/v1/storefront/theme', { body })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: storefrontKeys.all }),
  });
}

export function useSitePage(pageId: string | undefined) {
  return useQuery({
    queryKey: storefrontKeys.page(pageId ?? ''),
    queryFn: () =>
      unwrap(
        api.GET('/api/v1/storefront/pages/{pageId}', { params: { path: { pageId: pageId! } } }),
      ),
    enabled: Boolean(pageId),
  });
}

/**
 * Saves a page whole: its details and every block, in the order they appear.
 *
 * The revision the editor loaded goes back with it. If somebody else saved in the meantime the API
 * refuses with 409 rather than letting the second save quietly wipe out the first.
 */
export function useSaveSitePage(pageId: string) {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (body: Schemas['SaveSitePageRequest']) =>
      unwrap(api.PUT('/api/v1/storefront/pages/{pageId}', { params: { path: { pageId } }, body })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: storefrontKeys.all }),
  });
}

export function useCreateSitePage() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (body: Schemas['CreateSitePageRequest']) =>
      unwrap(api.POST('/api/v1/storefront/pages', { body })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: storefrontKeys.all }),
  });
}

export function useDeleteSitePage() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (pageId: string) =>
      unwrap(api.DELETE('/api/v1/storefront/pages/{pageId}', { params: { path: { pageId } } })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: storefrontKeys.all }),
  });
}

export function useSiteVersions() {
  return useQuery({
    queryKey: storefrontKeys.versions(),
    queryFn: () => unwrap(api.GET('/api/v1/storefront/versions')),
  });
}

/** Freezes the draft as a version. Publishing always publishes a version, never the draft itself. */
export function useStageSiteVersion() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: () => unwrap(api.POST('/api/v1/storefront/versions/stage')),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: storefrontKeys.all }),
  });
}

export function usePublishSiteVersion() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (versionId: string) =>
      unwrap(
        api.POST('/api/v1/storefront/versions/{versionId}/publish', {
          params: { path: { versionId } },
        }),
      ),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: storefrontKeys.all }),
  });
}

/** Puts a version that has been live before back in front of travellers. */
export function useRollBackSiteVersion() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (versionId: string) =>
      unwrap(
        api.POST('/api/v1/storefront/versions/{versionId}/rollback', {
          params: { path: { versionId } },
        }),
      ),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: storefrontKeys.all }),
  });
}

/** A short-lived link that shows an unpublished version on the site's own address. */
export function useCreatePreviewLink() {
  return useMutation({
    mutationFn: (versionId: string | null) =>
      unwrap(api.POST('/api/v1/storefront/preview-links', { body: { versionId } })),
  });
}

export function useSiteDomains() {
  return useQuery({
    queryKey: storefrontKeys.domains(),
    queryFn: () => unwrap(api.GET('/api/v1/storefront/domains')),
  });
}

export function useAddSiteDomain() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (hostname: string) =>
      unwrap(api.POST('/api/v1/storefront/domains', { body: { hostname } })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: storefrontKeys.all }),
  });
}

export function useRemoveSiteDomain() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (domainId: string) =>
      unwrap(
        api.DELETE('/api/v1/storefront/domains/{domainId}', { params: { path: { domainId } } }),
      ),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: storefrontKeys.all }),
  });
}

export function useMakeDomainPrimary() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (domainId: string) =>
      unwrap(
        api.POST('/api/v1/storefront/domains/{domainId}/primary', {
          params: { path: { domainId } },
        }),
      ),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: storefrontKeys.all }),
  });
}

/**
 * Looks for the hostname's DNS records now rather than waiting for the next scheduled check.
 *
 * The agent has just created the records at their registrar and wants to know whether they took, so
 * this exists for the same reason the "check now" button does: waiting is the worst part.
 */
export function useCheckDomainNow() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (domainId: string) =>
      unwrap(
        api.POST('/api/v1/storefront/domains/{domainId}/check', {
          params: { path: { domainId } },
        }),
      ),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: storefrontKeys.all }),
  });
}
