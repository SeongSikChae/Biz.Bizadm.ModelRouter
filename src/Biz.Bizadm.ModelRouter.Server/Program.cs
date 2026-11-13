
using Biz.Bizadm.KMS.Cipher;
using Biz.Bizadm.KMS.Credentials.Cipher;
using Biz.Bizadm.ModelRouter.Server.Middleware;
using Biz.Bizadm.ModelRouter.Server.Models.Provider;
using CommandLine;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Serilog;
using Serilog.Configuration;
using System.Configuration;
using System.Net.Security;
using System.Security.AccessControl;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using YamlDotNet.Serialization;

namespace Biz.Bizadm.ModelRouter.Server
{
    public class Program
    {
        private const string KMS_KEK_SERVICE = "biz-bizadm-modelrouter-server://kek";
        private const string KMS_KEK_ACCOUNT = "default";

        public sealed class CmdMain
        {
            [Option("config", Required = true, HelpText = "config file path")]
            public string ConfigFilePath { get; set; } = null!;

            [Option("log", Required = true, HelpText = "log dir path")]
            public string LogDirPath { get; set; } = null!;
        }

        public static async Task Main(string[] args)
        {
            ParserResult<CmdMain> result = await Parser.Default.ParseArguments<CmdMain>(args)
                .WithParsedAsync(async cmdMain =>
                {
                    YamlDotNet.Serialization.Deserializer deserializer = new YamlDotNet.Serialization.Deserializer();
                    FileInfo configFileInfo = new FileInfo(cmdMain.ConfigFilePath);
                    string str = File.ReadAllText(configFileInfo.FullName);
                    Configuration? configuration = deserializer.Deserialize<Configuration>(str);
                    ConfigurationValidator.Validate(configuration);
                    ProtectSecrets(configFileInfo, configuration);
                    WebApplication app = CreateWebApplication(args, cmdMain, configuration);
                    await StartAsync(app, cmdMain, configuration);
                });
        }

        private static void ProtectSecrets(FileInfo configFileInfo, Configuration configuration)
        {
            bool rewrite = false;

            if (!string.IsNullOrWhiteSpace(configuration.Kms.CredentialStore))
                Environment.SetEnvironmentVariable("GCM_CREDENTIAL_STORE", configuration.Kms.CredentialStore);

            FileInfo dekFileInfo = new FileInfo(Path.Combine(configuration.ContentRootPath, configuration.Kms.DekFilePath));

            IOsKekCredentialStore credentialStore = OsKekCredentialProvider.CreateForCurrentOs(KMS_KEK_SERVICE, KMS_KEK_ACCOUNT);
            try
            {
                CryptographicOperations.ZeroMemory(credentialStore.GetPassword());
            }
            catch (InvalidOperationException e) when (e.Message.StartsWith("KEK credential not found", StringComparison.Ordinal))
            {
                if (dekFileInfo.Exists)
                    throw new InvalidOperationException($"KEK credential not found but DEK file exists: {dekFileInfo.FullName}", e);

                byte[] password = RandomNumberGenerator.GetBytes(32);
                credentialStore.StorePassword(password);
                CryptographicOperations.ZeroMemory(password);
            }

            if (string.IsNullOrWhiteSpace(configuration.Kms.KekSalt))
            {
                configuration.Kms.KekSalt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
                WriteConfiguration(configFileInfo, configuration);
            }

            using AesGcmKekManager kekManager = AesGcmKekManager.Create(credentialStore, Convert.FromBase64String(configuration.Kms.KekSalt), configuration.Kms.KekIterations!.Value);
            using AesGcmDekCipher dekCipher = AesGcmDekCipher.Create(kekManager.Current, dekFileInfo);
            using DekCipherService dekCipherService = new DekCipherService(dekCipher, leaveOpen: true);

            string EncryptIfNeeded(string value)
            {
                if (dekCipherService.StartsWithFromPrefix(value))
                    return value;
                rewrite = true;
                return dekCipherService.Encrypt(value);
            }

            Configuration.SslConfiguration? ssl = configuration.Ssl;
            bool hasCertificatePassword = ssl?.ServerCertificatePassword is not null;

            if (hasCertificatePassword)
                ssl!.ServerCertificatePassword = EncryptIfNeeded(ssl.ServerCertificatePassword);
            foreach (Configuration.ProviderConfiguration provider in configuration.Providers.Values)
                provider.Key = EncryptIfNeeded(provider.Key);

            if (rewrite)
                WriteConfiguration(configFileInfo, configuration);

            if (hasCertificatePassword)
                ssl!.ServerCertificatePassword = dekCipherService.Decrypt(ssl.ServerCertificatePassword);
            foreach (Configuration.ProviderConfiguration provider in configuration.Providers.Values)
                provider.Key = dekCipherService.Decrypt(provider.Key);
        }

        internal static ISerializer CreateConfigurationSerializer()
            => new SerializerBuilder()
                .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
                .Build();

        private static void WriteConfiguration(FileInfo configFileInfo, Configuration configuration)
        {
            ISerializer serializer = CreateConfigurationSerializer();
            string tempFilePath = configFileInfo.FullName + ".tmp";
            try
            {
                File.WriteAllText(tempFilePath, serializer.Serialize(configuration));
                if (OperatingSystem.IsWindows())
                {
                    FileSecurity security = new FileSecurity();
                    security.SetSecurityDescriptorBinaryForm(configFileInfo.GetAccessControl().GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
                    new FileInfo(tempFilePath).SetAccessControl(security);
                }
                else
                    File.SetUnixFileMode(tempFilePath, File.GetUnixFileMode(configFileInfo.FullName));
                File.Move(tempFilePath, configFileInfo.FullName, overwrite: true);
            }
            catch
            {
                File.Delete(tempFilePath);
                throw;
            }
        }

        private static WebApplication CreateWebApplication(string[] args, CmdMain cmdMain, Configuration configuration)
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = args,
                ContentRootPath = configuration.ContentRootPath
            });

            builder.Logging.Services.AddSerilog(configureLogger =>
            {
                configureLogger
                    .MinimumLevel.Information()
                    .MinimumLevel.Override(
                        "Biz.Bizadm.ModelRouter.Server.Models.Provider.Azure.Foundry.AzureFoundryChatClient",
                        Serilog.Events.LogEventLevel.Debug)
                    .MinimumLevel.Override(
                        "Biz.Bizadm.ModelRouter.Server.Models.Provider.Anthropic.AnthropicMessagesClient",
                        Serilog.Events.LogEventLevel.Debug)
                    .Enrich.WithCaller()
                    .WriteTo.Console(
                        Serilog.Events.LogEventLevel.Debug,
                        "[RequestId={RequestId}] " + CallerEnricherOutputTemplate.Default
                    )
                    .WriteTo.File(
                        new DirectoryInfo(cmdMain.LogDirPath).FullName,
                        "Biz.Bizadm.ModelRouter.Server.log",
                        Serilog.Events.LogEventLevel.Debug,
                        "[RequestId={RequestId}] " + CallerEnricherOutputTemplate.Default,
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: 12
                    );
            });

            builder.WebHost.UseKestrel(options =>
            {
                options.ListenAnyIP(configuration.Port!.Value, configure =>
                {
                    configure.Protocols = HttpProtocols.Http1AndHttp2AndHttp3;
                    Configuration.SslConfiguration? ssl = configuration.Ssl;
                    if (ssl?.UseSsl == true)
                    {
                        X509Certificate2 certificate = X509CertificateLoader.LoadPkcs12FromFile(
                            new FileInfo(Path.Combine(configuration.ContentRootPath, ssl.ServerCertificate)).FullName,
                            ssl.ServerCertificatePassword
                        );

                        configure.UseHttps(httpsOptions =>
                        {
                            httpsOptions.ServerCertificate = certificate;

                            if (ssl.CertificateChain is not null)
                            {
                                X509Certificate2Collection collection = X509CertificateLoader.LoadPkcs12CollectionFromFile(
                                    new FileInfo(Path.Combine(configuration.ContentRootPath, ssl.ServerCertificate)).FullName,
                                    ssl.ServerCertificatePassword
                                );

                                Dictionary<string, X509Certificate2> certificates = new Dictionary<string, X509Certificate2>();
                                foreach (X509Certificate2 certificate in collection.Where(cert => !cert.HasPrivateKey))
                                {
                                    string name = certificate.GetNameInfo(X509NameType.SimpleName, false);
                                    certificates.Add(name, certificate);
                                }

                                string[] certificateChain =
                                    ssl.CertificateChain.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

                                httpsOptions.ServerCertificateChain = new X509Certificate2Collection();

                                foreach (string name in certificateChain)
                                {
                                    if (certificates.TryGetValue(name, out X509Certificate2? certificate))
                                        httpsOptions.ServerCertificateChain.Add(certificate);
                                }
                            }

                            httpsOptions.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
                            if (ssl.IncludeCipherSuites is not null)
                            {
                                string[] includeCipherSuites =
                                ssl.IncludeCipherSuites.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

                                if (includeCipherSuites.Length > 0)
                                {
                                    httpsOptions.OnAuthenticate = (connectionContext, authenticationOptions) =>
                                    {
                                        authenticationOptions.AllowRenegotiation = false;
                                        HashSet<TlsCipherSuite> tlsCipherSuites = new HashSet<TlsCipherSuite>();
                                        foreach (string cipherSuite in includeCipherSuites)
                                        {
                                            if (Enum.TryParse(cipherSuite, out TlsCipherSuite tlsCipherSuite))
                                                tlsCipherSuites.Add(tlsCipherSuite);
                                        }


                                        if (tlsCipherSuites.Count > 0)
                                            authenticationOptions.CipherSuitesPolicy = new CipherSuitesPolicy(tlsCipherSuites);
                                    };
                                }
                            }
                        });
                    }
                });

                options.ConfigureEndpointDefaults(configureOptions =>
                {
                    configureOptions.Protocols = HttpProtocols.Http1AndHttp2AndHttp3;
                });
            });

            builder.Services.AddSystemd();
            builder.Services.AddWindowsService();
            builder.Services.AddSingleton(configuration);

            builder.Services.AddHttpClient(ProviderClientRegistry.HttpClientName, httpClient =>
            {
                httpClient.Timeout = Timeout.InfiniteTimeSpan;
            });
            builder.Services.AddSingleton<ProviderClientRegistry>();
            builder.Services.AddSingleton<ModelRouteResolver>();

            builder.Services.AddControllers();
            // Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
            builder.Services.AddOpenApi();

            return builder.Build();
        }

        private static async Task StartAsync(WebApplication app, CmdMain cmdMain, Configuration configuration)
        {
            // 라우트 표를 기동 시점에 만들어 ReasoningByModel 키 경고를 바로 남긴다.
            app.Services.GetRequiredService<ModelRouteResolver>();

            // Configure the HTTP request pipeline.
            if (app.Environment.IsDevelopment())
            {
                app.MapOpenApi();
            }

            app.UseMiddleware<RequestLoggingMiddleware>();
            app.UseAuthorization();

            app.MapControllers();

            await app.RunAsync();
        }
    }
}
