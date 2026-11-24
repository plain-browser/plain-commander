namespace PlainCommander.Core.Remote;

/// <summary>The S3 services the connection form knows by name, in the order of its list.</summary>
public enum S3Provider
{
    /// <summary>Not chosen (a site saved before the form had the list): told from the host name.</summary>
    FromAddress,
    Aws,
    CloudflareR2,
    BackblazeB2,
    Wasabi,

    /// <summary>MinIO, RustFS, Ceph, a NAS … anything with an address of its own.</summary>
    Other,
}

/// <summary>What the known S3 services have in common: how their addresses are built and which region is in them.</summary>
public static class S3Endpoints
{
    /// <summary>The AWS regions offered in the form; any other can be typed.</summary>
    public static IReadOnlyList<string> AwsRegions { get; } =
    [
        "eu-central-1", "eu-central-2", "eu-west-1", "eu-west-2", "eu-west-3", "eu-north-1", "eu-south-1", "eu-south-2",
        "us-east-1", "us-east-2", "us-west-1", "us-west-2", "ca-central-1", "ca-west-1", "sa-east-1",
        "ap-south-1", "ap-south-2", "ap-southeast-1", "ap-southeast-2", "ap-southeast-3", "ap-southeast-4",
        "ap-northeast-1", "ap-northeast-2", "ap-northeast-3", "ap-east-1",
        "me-south-1", "me-central-1", "il-central-1", "af-south-1",
    ];

    public static string Name(S3Provider provider) => provider switch
    {
        S3Provider.Aws => "Amazon S3",
        S3Provider.CloudflareR2 => "Cloudflare R2",
        S3Provider.BackblazeB2 => "Backblaze B2",
        S3Provider.Wasabi => "Wasabi",
        _ => string.Empty,
    };

    /// <summary>What an address of the service looks like, for the placeholder of the endpoint field.</summary>
    public static string Example(S3Provider provider) => provider switch
    {
        S3Provider.Aws => "s3.eu-central-1.amazonaws.com",
        S3Provider.CloudflareR2 => "0123456789abcdef0123456789abcdef.r2.cloudflarestorage.com",
        S3Provider.BackblazeB2 => "s3.eu-central-003.backblazeb2.com",
        S3Provider.Wasabi => "s3.eu-central-1.wasabisys.com",
        _ => string.Empty,
    };

    /// <summary>The services of the form's list, in its order.</summary>
    public static IReadOnlyList<S3Provider> Listed { get; } = [S3Provider.Aws, S3Provider.CloudflareR2, S3Provider.BackblazeB2, S3Provider.Wasabi, S3Provider.Other];

    /// <summary>Where a service stands in <see cref="Listed"/>; one not there counts as <see cref="S3Provider.Other"/>.</summary>
    public static int ListIndex(S3Provider provider)
    {
        for (var i = 0; i < Listed.Count; i++)
        {
            if (Listed[i] == provider)
            {
                return i;
            }
        }

        return Listed.Count - 1;
    }

    /// <summary>The service a host name belongs to; <see cref="S3Provider.Other"/> for an unknown or empty one.</summary>
    public static S3Provider FromHost(string? host)
    {
        var name = (host ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
        return name switch
        {
            _ when name.EndsWith(".amazonaws.com", StringComparison.Ordinal) || name.EndsWith(".amazonaws.com.cn", StringComparison.Ordinal) => S3Provider.Aws,
            _ when name.EndsWith(".r2.cloudflarestorage.com", StringComparison.Ordinal) => S3Provider.CloudflareR2,
            _ when name.EndsWith(".backblazeb2.com", StringComparison.Ordinal) => S3Provider.BackblazeB2,
            _ when name.EndsWith(".wasabisys.com", StringComparison.Ordinal) => S3Provider.Wasabi,
            _ => S3Provider.Other,
        };
    }

    /// <summary>The service of a saved site: the one chosen in the form, else the one its host name tells.</summary>
    public static S3Provider ProviderOf(RemoteSite site)
    {
        ArgumentNullException.ThrowIfNull(site);
        return site.Provider is S3Provider.FromAddress || !Enum.IsDefined(site.Provider) ? FromHost(site.Host) : site.Provider;
    }

    /// <summary>"eu-central-1" → "s3.eu-central-1.amazonaws.com"; no region is the global address (us-east-1).</summary>
    public static string AwsHost(string? region)
    {
        var name = (region ?? string.Empty).Trim().ToLowerInvariant();
        return name.Length == 0 ? "s3.amazonaws.com"
            : name.StartsWith("cn-", StringComparison.Ordinal) ? $"s3.{name}.amazonaws.com.cn"
            : $"s3.{name}.amazonaws.com";
    }

    /// <summary>
    /// The region requests to a host are signed for: the one in the name at AWS, Backblaze B2 and Wasabi
    /// ("s3.eu-central-1.amazonaws.com" → "eu-central-1"), "auto" at Cloudflare R2, otherwise "us-east-1".
    /// </summary>
    public static string RegionOf(string? host)
    {
        var name = (host ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant();
        switch (FromHost(name))
        {
            case S3Provider.CloudflareR2:
                return "auto";
            case S3Provider.Other:
                return "us-east-1";
        }

        foreach (var part in name.Split('.'))
        {
            var region = part.StartsWith("s3-", StringComparison.Ordinal) ? part[3..] : part;
            if (region.Count(c => c == '-') >= 2 && region.Any(char.IsDigit))
            {
                return region; // eu-central-1, us-west-004 …
            }
        }

        return "us-east-1";
    }

    /// <summary>
    /// What was typed or pasted into the endpoint field, taken apart: "https://minio.example.com:9000/" is the host
    /// "minio.example.com", port 9000 over HTTPS. A bare host name has no port (0) and no scheme (null).
    /// </summary>
    public static (string Host, int Port, bool? PlainHttp) ParseEndpoint(string? text)
    {
        var rest = (text ?? string.Empty).Trim();
        bool? http = null;
        if (rest.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            http = true;
            rest = rest[7..];
        }
        else if (rest.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            http = false;
            rest = rest[8..];
        }

        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        if (slash >= 0)
        {
            rest = rest[..slash];
        }

        var port = 0;
        var colon = rest.LastIndexOf(':');
        if (colon >= 0 && !rest.EndsWith(']') && int.TryParse(rest[(colon + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var p) && p is > 0 and < 65536)
        {
            port = p;
            rest = rest[..colon];
        }

        return (rest.Trim(), port, http);
    }
}
