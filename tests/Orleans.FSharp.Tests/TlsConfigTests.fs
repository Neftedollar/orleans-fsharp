module Orleans.FSharp.Tests.TlsConfigTests

open System.Security.Cryptography
open System.Security.Cryptography.X509Certificates
open Xunit
open Swensen.Unquote
open Orleans.FSharp.Runtime

/// <summary>Creates a self-signed X509Certificate2 for testing purposes.</summary>
let createTestCert () =
    use rsa = RSA.Create(2048)
    let req = CertificateRequest("CN=Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
    let cert = req.CreateSelfSigned(System.DateTimeOffset.UtcNow, System.DateTimeOffset.UtcNow.AddYears(1))
    let pfxBytes = cert.Export(X509ContentType.Pfx)
    X509CertificateLoader.LoadPkcs12(pfxBytes, null)

/// <summary>Helper to check if a TlsConfig is TlsSubject.</summary>
let isTlsSubject =
    function
    | TlsSubject _ -> true
    | _ -> false

/// <summary>Helper to check if a TlsConfig is TlsCertificate.</summary>
let isTlsCertificate =
    function
    | TlsCertificate _ -> true
    | _ -> false

/// <summary>Helper to check if a TlsConfig is MutualTlsSubject.</summary>
let isMutualTlsSubject =
    function
    | MutualTlsSubject _ -> true
    | _ -> false

/// <summary>Helper to check if a TlsConfig is MutualTlsCertificate.</summary>
let isMutualTlsCertificate =
    function
    | MutualTlsCertificate _ -> true
    | _ -> false

// --- Silo TLS tests ---

[<Fact>]
let ``siloConfig CE default has no TLS`` () =
    let config = siloConfig { () }
    test <@ config.TlsConfig.IsNone @>

[<Fact>]
let ``siloConfig CE sets useTls with subject`` () =
    let config = siloConfig { useTls "my-cert-subject" }
    test <@ config.TlsConfig.IsSome @>
    test <@ config.TlsConfig.Value |> isTlsSubject @>

[<Fact>]
let ``siloConfig CE useTls stores subject name`` () =
    let config = siloConfig { useTls "my-cert-subject" }

    match config.TlsConfig.Value with
    | TlsSubject subject -> test <@ subject = "my-cert-subject" @>
    | other -> failwith $"Expected TlsSubject, got {other}"

[<Fact>]
let ``siloConfig CE sets useTlsWithCertificate`` () =
    use cert = createTestCert ()
    let config = siloConfig { useTlsWithCertificate cert }
    test <@ config.TlsConfig.IsSome @>
    test <@ config.TlsConfig.Value |> isTlsCertificate @>

[<Fact>]
let ``siloConfig CE sets useMutualTls with subject`` () =
    let config = siloConfig { useMutualTls "mtls-subject" }
    test <@ config.TlsConfig.IsSome @>
    test <@ config.TlsConfig.Value |> isMutualTlsSubject @>

[<Fact>]
let ``siloConfig CE useMutualTls stores subject name`` () =
    let config = siloConfig { useMutualTls "mtls-subject" }

    match config.TlsConfig.Value with
    | MutualTlsSubject subject -> test <@ subject = "mtls-subject" @>
    | other -> failwith $"Expected MutualTlsSubject, got {other}"

[<Fact>]
let ``siloConfig CE sets useMutualTlsWithCertificate`` () =
    use cert = createTestCert ()
    let config = siloConfig { useMutualTlsWithCertificate cert }
    test <@ config.TlsConfig.IsSome @>
    test <@ config.TlsConfig.Value |> isMutualTlsCertificate @>

[<Fact>]
let ``siloConfig CE TLS composes with other options`` () =
    let config =
        siloConfig {
            useLocalhostClustering
            addMemoryStorage "Default"
            useTls "my-cert"
        }

    test <@ config.ClusteringMode.IsSome @>
    test <@ config.StorageProviders |> Map.containsKey "Default" @>
    test <@ config.TlsConfig.IsSome @>
    test <@ config.TlsConfig.Value |> isTlsSubject @>

[<Fact>]
let ``siloConfig CE later TLS overrides earlier`` () =
    let config =
        siloConfig {
            useTls "first-cert"
            useMutualTls "mtls-cert"
        }

    test <@ config.TlsConfig.Value |> isMutualTlsSubject @>

// --- Client TLS tests ---

[<Fact>]
let ``clientConfig CE default has no TLS`` () =
    let config = clientConfig { () }
    test <@ config.TlsConfig.IsNone @>

[<Fact>]
let ``clientConfig CE sets useTls with subject`` () =
    let config = clientConfig { useTls "client-cert" }
    test <@ config.TlsConfig.IsSome @>
    test <@ config.TlsConfig.Value |> isTlsSubject @>

[<Fact>]
let ``clientConfig CE useTls stores subject name`` () =
    let config = clientConfig { useTls "client-cert" }

    match config.TlsConfig.Value with
    | TlsSubject subject -> test <@ subject = "client-cert" @>
    | other -> failwith $"Expected TlsSubject, got {other}"

[<Fact>]
let ``clientConfig CE sets useTlsWithCertificate`` () =
    use cert = createTestCert ()
    let config = clientConfig { useTlsWithCertificate cert }
    test <@ config.TlsConfig.IsSome @>
    test <@ config.TlsConfig.Value |> isTlsCertificate @>

[<Fact>]
let ``clientConfig CE sets useMutualTls`` () =
    let config = clientConfig { useMutualTls "mtls-subject" }
    test <@ config.TlsConfig.IsSome @>
    test <@ config.TlsConfig.Value |> isMutualTlsSubject @>

[<Fact>]
let ``clientConfig CE TLS composes with clustering`` () =
    let config =
        clientConfig {
            useLocalhostClustering
            useTls "cert-subject"
        }

    test <@ config.ClusteringMode.IsSome @>
    test <@ config.TlsConfig.IsSome @>
    test <@ config.TlsConfig.Value |> isTlsSubject @>

[<Fact>]
let ``clientConfig CE later TLS overrides earlier`` () =
    let config =
        clientConfig {
            useTls "first-cert"
            useMutualTls "mtls-cert"
        }

    test <@ config.TlsConfig.Value |> isMutualTlsSubject @>
