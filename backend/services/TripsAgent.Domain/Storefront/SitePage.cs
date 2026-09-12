using TripsAgent.Domain.Common;

namespace TripsAgent.Domain.Storefront;

/// <summary>
/// One page of a site's draft: its address, its title, and the blocks it is built from.
/// </summary>
/// <remarks>
/// <para>
/// Pages exist only in the draft. Staging copies them into a snapshot, so a published site is
/// never read from these rows — which is why editing one can never change what travellers see
/// until the agent publishes.
/// </para>
/// <para>
/// A page saves whole: its details and every block in order, in one request. Reordering is the
/// same save with the blocks in a new order, so a failed reorder leaves nothing half-moved.
/// <see cref="Revision"/> catches two browser tabs saving over each other.
/// </para>
/// </remarks>
public sealed class SitePage : Entity, IAuditableEntity, ITenantScoped
{
    public const int MaxBlocks = 30;

    private readonly List<SiteBlock> _blocks = [];

    private SitePage()
    {
        Slug = string.Empty;
        Title = string.Empty;
    }

    /// <summary>A new page in <paramref name="draft"/>.</summary>
    public static SitePage Create(
        SiteVersion draft,
        SitePageType pageType,
        string slug,
        string title,
        bool showInNav,
        int position)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentOutOfRangeException.ThrowIfNegative(position);

        if (draft.Status != SiteVersionStatus.Draft)
        {
            throw new InvalidOperationException("Pages are only ever added to the draft.");
        }

        return new SitePage
        {
            AgencyId = draft.AgencyId,
            VersionId = draft.Id,
            PageType = pageType,
            IsSystem = pageType != SitePageType.Custom,
            Slug = pageType == SitePageType.Home ? SitePageRules.HomeSlug : SitePageRules.RequireSlug(slug),
            Title = SitePageRules.RequireTitle(title),
            ShowInNav = showInNav,
            Position = position,
        };
    }

    public Guid AgencyId { get; private set; }

    /// <summary>The draft version this page belongs to.</summary>
    public Guid VersionId { get; private set; }

    /// <summary>The page's address. <c>home</c> is the site's root and cannot be changed.</summary>
    public string Slug { get; private set; }

    public SitePageType PageType { get; private set; }

    public string Title { get; private set; }

    /// <summary>True for the pages every site has. They can be edited, never deleted.</summary>
    public bool IsSystem { get; private set; }

    /// <summary>Listed in the site's navigation.</summary>
    public bool ShowInNav { get; private set; }

    /// <summary>Order in the navigation. The home page is always first.</summary>
    public int Position { get; private set; }

    /// <summary>The page's title in search results, when it should differ from <see cref="Title"/>.</summary>
    public string? MetaTitle { get; private set; }

    public string? MetaDescription { get; private set; }

    /// <summary>
    /// Bumped by every save. A save that names an older revision is refused, so a second browser
    /// tab cannot silently overwrite the first.
    /// </summary>
    public int Revision { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>The blocks, in the order they appear.</summary>
    public IReadOnlyList<SiteBlock> Blocks => _blocks;

    /// <summary>Changes the page's title, address, visibility and search details.</summary>
    public void UpdateDetails(string title, string? slug, bool showInNav, string? metaTitle, string? metaDescription)
    {
        Title = SitePageRules.RequireTitle(title);

        if (PageType != SitePageType.Home && !string.IsNullOrWhiteSpace(slug))
        {
            Slug = SitePageRules.RequireSlug(slug);
        }

        // The home page is where the navigation starts; hiding it would leave the logo as the
        // only way back.
        ShowInNav = PageType == SitePageType.Home || showInNav;
        MetaTitle = SitePageRules.OptionalText(metaTitle, SitePageRules.MaxMetaTitleLength, nameof(metaTitle));
        MetaDescription = SitePageRules.OptionalText(metaDescription, SitePageRules.MaxMetaDescriptionLength, nameof(metaDescription));
    }

    /// <summary>
    /// Replaces the page's blocks with <paramref name="blocks"/>, in that order.
    /// </summary>
    /// <remarks>
    /// Blocks named by an id this page already has are updated in place; the rest are added, and any
    /// the list leaves out are removed. Positions are rewritten from 0, so there are never gaps or
    /// duplicates. The caller has validated every config already.
    /// </remarks>
    public void ReplaceBlocks(IReadOnlyList<SiteBlockContent> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);

        if (blocks.Count > MaxBlocks)
        {
            throw new ArgumentException($"A page can hold at most {MaxBlocks} blocks.", nameof(blocks));
        }

        var kept = new HashSet<Guid>();
        var ordered = new List<SiteBlock>(blocks.Count);

        for (var position = 0; position < blocks.Count; position++)
        {
            var content = blocks[position];
            var existing = content.Id is { } id ? _blocks.FirstOrDefault(block => block.Id == id) : null;

            if (existing is not null && existing.BlockType == content.BlockType && kept.Add(existing.Id))
            {
                existing.Update(content.Config, position);
                ordered.Add(existing);
            }
            else
            {
                ordered.Add(SiteBlock.Create(this, content.BlockType, content.Config, position));
            }
        }

        _blocks.RemoveAll(block => !kept.Contains(block.Id));

        foreach (var block in ordered.Where(block => !_blocks.Contains(block)))
        {
            _blocks.Add(block);
        }

        _blocks.Sort((left, right) => left.Position.CompareTo(right.Position));
        Revision++;
    }

    /// <summary>Records a save that changed only the page's details, not its blocks.</summary>
    public void Touch() => Revision++;
}

/// <summary>One block as a page save describes it.</summary>
/// <param name="Id">The block's id when it already exists on the page; null for a new one.</param>
/// <param name="BlockType">What kind of block.</param>
/// <param name="Config">Its settings, as validated JSON.</param>
public sealed record SiteBlockContent(Guid? Id, SiteBlockType BlockType, string Config);

/// <summary>
/// One section of a page — a hero, a gallery, an FAQ — and its settings.
/// </summary>
/// <remarks>
/// <see cref="Config"/> is JSON whose shape depends on <see cref="BlockType"/>. The application
/// validates it against the block registry before it is saved, so what is stored always parses.
/// </remarks>
public sealed class SiteBlock : Entity, IAuditableEntity, ITenantScoped
{
    private SiteBlock()
    {
        Config = "{}";
    }

    public Guid AgencyId { get; private set; }

    public Guid PageId { get; private set; }

    public SiteBlockType BlockType { get; private set; }

    /// <summary>From 0, with no gaps. Unique per page, checked by the database at commit.</summary>
    public int Position { get; private set; }

    /// <summary>The block's settings, as JSON. Never a price: prices come from the catalog at render time.</summary>
    public string Config { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    internal static SiteBlock Create(SitePage page, SiteBlockType blockType, string config, int position)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(config);

        return new SiteBlock
        {
            AgencyId = page.AgencyId,
            PageId = page.Id,
            BlockType = blockType,
            Config = config,
            Position = position,
        };
    }

    internal void Update(string config, int position)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(config);

        Config = config;
        Position = position;
    }
}
