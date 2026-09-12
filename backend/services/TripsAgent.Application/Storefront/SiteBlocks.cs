using System.Text.Json;
using System.Text.Json.Serialization;
using TripsAgent.Contracts.Storefront;
using TripsAgent.Domain.Catalog;
using TripsAgent.Domain.Storefront;

namespace TripsAgent.Application.Storefront;

/// <summary>
/// Blocks: how the wire shape becomes what is stored and back again, and the rules each type follows.
/// </summary>
public static class SiteBlocks
{
    public const int MaxHeadingLength = 120;
    public const int MaxSubheadingLength = 300;
    public const int MaxCtaLabelLength = 40;
    public const int MaxLinkLength = 500;
    public const int MaxTextBodyLength = 5000;
    public const int MaxIntroLength = 300;
    public const int MaxProductGridItems = 12;

    public const string LatestMode = "Latest";
    public const string SelectedMode = "Selected";

    /// <summary>How block settings and snapshots are written: camelCase, with empty settings left out.</summary>
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static SiteBlockDto Hero(HeroBlockConfig config) => new(SiteBlockTypes.Hero, config, null, null, null);

    public static SiteBlockDto ProductGrid(ProductGridBlockConfig config) => new(SiteBlockTypes.ProductGrid, null, config, null, null);

    public static SiteBlockDto Text(TextBlockConfig config) => new(SiteBlockTypes.Text, null, null, config, null);

    public static SiteBlockDto Contact(ContactBlockConfig config) => new(SiteBlockTypes.Contact, null, null, null, config);

    /// <summary>
    /// A stored block, back in its wire shape. Null when its settings cannot be read, so a renderer skips
    /// that one block rather than failing the page.
    /// </summary>
    public static SiteBlockDto? FromStored(SiteBlockType type, string config)
    {
        ArgumentNullException.ThrowIfNull(config);

        try
        {
            return type switch
            {
                SiteBlockType.Hero => JsonSerializer.Deserialize<HeroBlockConfig>(config, Json) is { } hero ? Hero(hero) : null,
                SiteBlockType.ProductGrid => JsonSerializer.Deserialize<ProductGridBlockConfig>(config, Json) is { } grid ? ProductGrid(grid) : null,
                SiteBlockType.Text => JsonSerializer.Deserialize<TextBlockConfig>(config, Json) is { } text ? Text(text) : null,
                SiteBlockType.Contact => JsonSerializer.Deserialize<ContactBlockConfig>(config, Json) is { } contact ? Contact(contact) : null,
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The settings of a validated block, as the JSON that is stored.</summary>
    public static string ToStored(SiteBlockDto block)
    {
        ArgumentNullException.ThrowIfNull(block);

        object? config = block.Type switch
        {
            SiteBlockTypes.Hero => block.Hero,
            SiteBlockTypes.ProductGrid => block.ProductGrid,
            SiteBlockTypes.Text => block.Text,
            SiteBlockTypes.Contact => block.Contact,
            _ => null,
        };

        return config is null
            ? throw new ArgumentException($"A {block.Type} block needs its settings.", nameof(block))
            : JsonSerializer.Serialize(config, config.GetType(), Json);
    }

    /// <summary>The stored type for a wire type name, matched exactly. False for anything else.</summary>
    public static bool TryParseType(string? name, out SiteBlockType type)
    {
        type = default;

        return name is not null
               && SiteBlockTypes.All.Contains(name, StringComparer.Ordinal)
               && Enum.TryParse(name, ignoreCase: false, out type);
    }
}

/// <summary>What validating a page's blocks produced: the clean blocks and what they point at.</summary>
public sealed class ValidatedBlocks
{
    /// <summary>Each block, trimmed and normalised, in order.</summary>
    public List<(SiteBlockType Type, SiteBlockDto Block)> Blocks { get; } = [];

    /// <summary>Every image a block uses, with the field to blame if it cannot be shown.</summary>
    public List<(Guid AssetId, string Field)> Images { get; } = [];

    /// <summary>Every product a grid names, with the field to blame if it is not the agency's.</summary>
    public List<(Guid ProductId, string Field)> Products { get; } = [];
}

/// <summary>
/// The rules for each block's settings. Checked on every save, so what is stored always renders.
/// </summary>
/// <remarks>
/// The field names in the errors match the console's form fields — <c>blocks[2].hero.heading</c> — so a
/// message lands next to the field that caused it.
/// </remarks>
public static class SiteBlockValidator
{
    private const string LinkCharacters = "-._~/?#=&%+";

    public static ValidatedBlocks Validate(IReadOnlyList<SiteBlockDto?> blocks, string field, FieldErrors errors)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(errors);

        var result = new ValidatedBlocks();

        if (blocks.Count > SitePage.MaxBlocks)
        {
            errors.Add(field, $"A page can hold at most {SitePage.MaxBlocks} blocks.");
            return result;
        }

        for (var index = 0; index < blocks.Count; index++)
        {
            var path = $"{field}[{index}]";
            var block = blocks[index];

            if (block is null)
            {
                errors.Add(path, "A block is missing its settings.");
                continue;
            }

            if (!SiteBlocks.TryParseType(block.Type, out var type))
            {
                errors.Add($"{path}.type", $"Choose one of: {string.Join(", ", SiteBlockTypes.All)}.");
                continue;
            }

            var given = new object?[] { block.Hero, block.ProductGrid, block.Text, block.Contact }.Count(config => config is not null);

            if (given > 1)
            {
                errors.Add(path, "Give the settings for one block type only.");
                continue;
            }

            var clean = type switch
            {
                SiteBlockType.Hero => ValidateHero(block.Hero, $"{path}.hero", errors, result),
                SiteBlockType.ProductGrid => ValidateProductGrid(block.ProductGrid, $"{path}.productGrid", errors, result),
                SiteBlockType.Text => ValidateText(block.Text, $"{path}.text", errors),
                SiteBlockType.Contact => ValidateContact(block.Contact, $"{path}.contact", errors),
                _ => null,
            };

            if (clean is not null)
            {
                result.Blocks.Add((type, clean));
            }
        }

        return result;
    }

    /// <summary>
    /// A path on the site (<c>/contact</c>) or an <c>https://</c> address. Never <c>javascript:</c>,
    /// never protocol-relative, never carrying a username: a link on a public page is an attack surface.
    /// </summary>
    public static bool IsSafeLink(string href)
    {
        ArgumentNullException.ThrowIfNull(href);

        if (href.Length > SiteBlocks.MaxLinkLength)
        {
            return false;
        }

        if (href.StartsWith('/'))
        {
            return !href.StartsWith("//", StringComparison.Ordinal)
                   && href.All(character => char.IsAsciiLetterOrDigit(character) || LinkCharacters.Contains(character, StringComparison.Ordinal));
        }

        return Uri.TryCreate(href, UriKind.Absolute, out var uri)
               && uri.Scheme == Uri.UriSchemeHttps
               && string.IsNullOrEmpty(uri.UserInfo)
               && !string.IsNullOrEmpty(uri.Host);
    }

    private static SiteBlockDto? ValidateHero(HeroBlockConfig? hero, string path, FieldErrors errors, ValidatedBlocks result)
    {
        if (hero is null)
        {
            errors.Add(path, "Fill in the banner's settings.");
            return null;
        }

        var heading = Required(hero.Heading, SiteBlocks.MaxHeadingLength, $"{path}.heading", "The banner needs a heading.", errors);
        var subheading = Optional(hero.Subheading, SiteBlocks.MaxSubheadingLength, $"{path}.subheading", errors);
        var label = Optional(hero.CtaLabel, SiteBlocks.MaxCtaLabelLength, $"{path}.ctaLabel", errors);
        var href = Optional(hero.CtaHref, SiteBlocks.MaxLinkLength, $"{path}.ctaHref", errors);

        if (label is null != (href is null))
        {
            errors.Add(label is null ? $"{path}.ctaLabel" : $"{path}.ctaHref", "A button needs both its words and where it goes.");
        }

        if (href is not null && !IsSafeLink(href))
        {
            errors.Add($"{path}.ctaHref", "Use a page on your site, like /contact, or an https:// address.");
        }

        if (hero.ImageAssetId is { } image)
        {
            result.Images.Add((image, $"{path}.imageAssetId"));
        }

        return SiteBlocks.Hero(new HeroBlockConfig(heading ?? string.Empty, subheading, hero.ImageAssetId, label, href));
    }

    private static SiteBlockDto? ValidateProductGrid(ProductGridBlockConfig? grid, string path, FieldErrors errors, ValidatedBlocks result)
    {
        if (grid is null)
        {
            errors.Add(path, "Fill in the product grid's settings.");
            return null;
        }

        var heading = Required(grid.Heading, SiteBlocks.MaxHeadingLength, $"{path}.heading", "The product grid needs a heading.", errors);

        var mode = string.Equals(grid.Mode, SiteBlocks.SelectedMode, StringComparison.OrdinalIgnoreCase) ? SiteBlocks.SelectedMode
            : string.Equals(grid.Mode, SiteBlocks.LatestMode, StringComparison.OrdinalIgnoreCase) ? SiteBlocks.LatestMode
            : null;

        if (mode is null)
        {
            errors.Add($"{path}.mode", "Choose Latest or Selected.");
        }

        string? productType = null;

        if (!string.IsNullOrWhiteSpace(grid.ProductType))
        {
            if (Enum.TryParse<ProductType>(grid.ProductType.Trim(), ignoreCase: true, out var parsed)
                && Enum.IsDefined(parsed)
                && !grid.ProductType.Trim().All(char.IsAsciiDigit))
            {
                productType = parsed.ToString();
            }
            else
            {
                errors.Add($"{path}.productType", "Choose Tour, Package or Visa — or leave it empty for all.");
            }
        }

        var ids = (grid.ProductIds ?? []).Distinct().ToList();

        if (mode == SiteBlocks.SelectedMode)
        {
            if (ids.Count == 0)
            {
                errors.Add($"{path}.productIds", "Choose at least one product to show.");
            }
            else if (ids.Count > SiteBlocks.MaxProductGridItems)
            {
                errors.Add($"{path}.productIds", $"Choose at most {SiteBlocks.MaxProductGridItems} products.");
            }
        }
        else
        {
            // The newest products are chosen when the page is shown; a list here would only mislead.
            ids = [];
        }

        if (grid.Limit is < 1 or > SiteBlocks.MaxProductGridItems)
        {
            errors.Add($"{path}.limit", $"Show between 1 and {SiteBlocks.MaxProductGridItems} products.");
        }

        for (var index = 0; index < ids.Count; index++)
        {
            result.Products.Add((ids[index], $"{path}.productIds[{index}]"));
        }

        return SiteBlocks.ProductGrid(new ProductGridBlockConfig(heading ?? string.Empty, mode ?? SiteBlocks.LatestMode, productType, ids, grid.Limit));
    }

    private static SiteBlockDto? ValidateText(TextBlockConfig? text, string path, FieldErrors errors)
    {
        if (text is null)
        {
            errors.Add(path, "Fill in the text block's settings.");
            return null;
        }

        var heading = Optional(text.Heading, SiteBlocks.MaxHeadingLength, $"{path}.heading", errors);
        var body = Required(text.Body, SiteBlocks.MaxTextBodyLength, $"{path}.body", "Write something for the text block.", errors);

        return SiteBlocks.Text(new TextBlockConfig(heading, body ?? string.Empty));
    }

    private static SiteBlockDto? ValidateContact(ContactBlockConfig? contact, string path, FieldErrors errors)
    {
        if (contact is null)
        {
            errors.Add(path, "Fill in the contact block's settings.");
            return null;
        }

        var heading = Required(contact.Heading, SiteBlocks.MaxHeadingLength, $"{path}.heading", "The contact block needs a heading.", errors);
        var intro = Optional(contact.Intro, SiteBlocks.MaxIntroLength, $"{path}.intro", errors);

        return SiteBlocks.Contact(contact with { Heading = heading ?? string.Empty, Intro = intro });
    }

    private static string? Required(string? value, int maxLength, string field, string missing, FieldErrors errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add(field, missing);
            return null;
        }

        return Optional(value, maxLength, field, errors);
    }

    private static string? Optional(string? value, int maxLength, string field, FieldErrors errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();

        if (trimmed.Length > maxLength)
        {
            errors.Add(field, $"Keep this to {maxLength} characters or fewer.");
        }

        return trimmed;
    }
}
