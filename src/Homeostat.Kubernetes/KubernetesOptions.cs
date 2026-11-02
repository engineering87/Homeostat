namespace Homeostat.Kubernetes;

/// <summary>
/// How Homeostat reaches the Kubernetes API server. The defaults work inside a pod: the API server from
/// <c>KUBERNETES_SERVICE_HOST</c> and <c>KUBERNETES_SERVICE_PORT</c>, the service account token, CA and namespace from
/// <c>/var/run/secrets/kubernetes.io/serviceaccount</c>, the pod name from <c>POD_NAME</c> (or <c>HOSTNAME</c>), and the
/// node name from <c>NODE_NAME</c>.
/// </summary>
public sealed class KubernetesOptions
{
    /// <summary>Default directory of the service account files mounted in every pod.</summary>
    public const string ServiceAccountDirectory = "/var/run/secrets/kubernetes.io/serviceaccount";

    /// <summary>API server address. Defaults to <c>https://{KUBERNETES_SERVICE_HOST}:{KUBERNETES_SERVICE_PORT}</c>.</summary>
    public Uri? ApiServer { get; set; }

    /// <summary>File with the bearer token; read again every minute, as projected tokens rotate. Defaults to the service account token mounted in the pod.</summary>
    public string TokenPath { get; set; } = ServiceAccountDirectory + "/token";

    /// <summary>
    /// CA certificate (PEM) that signs the API server certificate. The connection is trusted only through this CA or the
    /// system trust store; certificate validation is never disabled. Defaults to the service account CA mounted in the pod.
    /// </summary>
    public string CertificateAuthorityPath { get; set; } = ServiceAccountDirectory + "/ca.crt";

    /// <summary>Namespace of the application. Defaults to the content of the service account <c>namespace</c> file.</summary>
    public string? Namespace { get; set; }

    /// <summary>
    /// Name of this pod. Defaults to <c>POD_NAME</c> (set it with the downward API: <c>fieldRef: metadata.name</c>),
    /// then <c>HOSTNAME</c>, which differs from the pod name with <c>spec.hostname</c> or <c>hostNetwork</c>.
    /// </summary>
    public string? PodName { get; set; }

    /// <summary>Name of the node running this pod. Defaults to <c>NODE_NAME</c> (downward API: <c>fieldRef: spec.nodeName</c>).</summary>
    public string? NodeName { get; set; }

    /// <summary>Obtains the bearer token instead of reading <see cref="TokenPath"/>, for example outside a cluster.</summary>
    public Func<CancellationToken, ValueTask<string>>? AccessToken { get; set; }

    internal bool PodNameFromHostName { get; private set; }

    /// <summary>Timeout of one API request. Defaults to 30 seconds.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    internal static string? Read(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Fills the defaults from the environment and the service account files.</summary>
    internal void Resolve()
    {
        if (ApiServer is null
            && Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST") is { Length: > 0 } host)
        {
            var port = Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_PORT") is { Length: > 0 } p ? p : "443";
            // An IPv6 service address needs brackets.
            ApiServer = new Uri($"https://{(host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host)}:{port}");
        }

        Namespace ??= Read(ServiceAccountDirectory + "/namespace");
        if (PodName is null)
        {
            if (Environment.GetEnvironmentVariable("POD_NAME") is { Length: > 0 } pod)
            {
                PodName = pod;
            }
            else
            {
                // Usually the pod name, but not with spec.hostname or hostNetwork: RestartPod checks the pod exists and
                // has a controller before deleting it.
                PodName = Environment.GetEnvironmentVariable("HOSTNAME");
                PodNameFromHostName = PodName is not null;
            }
        }
        NodeName ??= Environment.GetEnvironmentVariable("NODE_NAME");
    }
}
