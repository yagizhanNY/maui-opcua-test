using System;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

namespace opc_ua_test.Services
{
    public class OpcUaService
    {
        const int ReconnectPeriod = 10;

        ApplicationInstance _application;
        ApplicationConfiguration _config;
        ApplicationConfiguration _certificateManagerConfig;
        SessionReconnectHandler _reconnectHandler;
        Session _session;

        bool _autoAccept = false;

        public async Task InitialClient(string ipAddress)
        {
            try
            {
                string endpointUrl = await CreateOpcInstance(ipAddress);

                bool haveAppCertificate = await _application.CheckApplicationInstanceCertificate(false, 0);
                if (!haveAppCertificate)
                {
                    throw new Exception("Application instance certificate invalid!");
                }

                if (haveAppCertificate)
                {
                    _config.ApplicationUri = X509Utils.GetApplicationUriFromCertificate(_config.SecurityConfiguration.ApplicationCertificate.Certificate);
                    if (_config.SecurityConfiguration.AutoAcceptUntrustedCertificates)
                    {
                        _autoAccept = true;
                    }
                    _config.CertificateValidator.CertificateValidation += new CertificateValidationEventHandler(CertificateValidator_CertificateValidation);
                }
                else
                {
                    Console.WriteLine("WARN: missing application certificate, using unsecure connection.");
                }

                var selectedEndpoint = CoreClientUtils.SelectEndpoint(endpointUrl, haveAppCertificate, 15000);

                string deviceType = selectedEndpoint.Server.ApplicationName.Text.Split('-')[0];


                var endpointConfiguration = EndpointConfiguration.Create(_config);
                var endpoint = new ConfiguredEndpoint(null, selectedEndpoint, endpointConfiguration);
                await CreateNewSession(endpoint);
            }
            catch(Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
            
        }

        private async Task<string> CreateOpcInstance(string ipAddress)
        {
            if (_application != null) _application = null;
            if (_config != null) _config = null;

            string endpointUrl = $"opc.tcp://{ipAddress}:4840";

            _application = new ApplicationInstance
            {
                ApplicationName = "Client",
                ApplicationType = ApplicationType.Client,
                ConfigSectionName = Utils.IsRunningOnMono() ||
                DeviceInfo.Current.Platform == DevicePlatform.iOS ||
                DeviceInfo.Current.Platform == DevicePlatform.MacCatalyst ? "Opc.Ua.MonoSampleClient" : "Opc.Ua.SampleClient"
            };

            try
            {
                using var stream = await FileSystem.OpenAppPackageFileAsync(_application.ConfigSectionName + ".Config.xml");
                _config = await _application.LoadApplicationConfiguration(stream, false);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }

            return endpointUrl;
        }

        private void CertificateValidator_CertificateValidation(CertificateValidator validator, CertificateValidationEventArgs e)
        {
            if (e.Error.StatusCode == StatusCodes.BadCertificateUntrusted)
            {
                e.Accept = _autoAccept;
                if (_autoAccept)
                {
                    Console.WriteLine("Accepted Certificate: {0}", e.Certificate.Subject);
                }
                else
                {
                    Console.WriteLine("Rejected Certificate: {0}", e.Certificate.Subject);
                }
            }
        }

        private async Task CreateNewSession(ConfiguredEndpoint endpoint)
        {
            if (_session != null)
            {
                _session.Close();
                _session.Dispose();
            }

            _session = await Session.Create(_config, endpoint, false, "OPC UA Client", 60000, new UserIdentity(new AnonymousIdentityToken()), null);

            // register keep alive handler
            _session.KeepAlive += Client_KeepAlive;
        }

        private void Client_KeepAlive(Session sender, KeepAliveEventArgs e)
        {
            if (e.Status != null && ServiceResult.IsNotGood(e.Status))
            {
                Console.WriteLine("{0} {1}/{2}", e.Status, sender.OutstandingRequestCount, sender.DefunctRequestCount);

                if (_reconnectHandler == null)
                {
                    Console.WriteLine("--- RECONNECTING ---");
                    _reconnectHandler = new SessionReconnectHandler();
                    _reconnectHandler.BeginReconnect(sender, ReconnectPeriod * 1000, Client_ReconnectComplete);
                }
            }
        }

        private void Client_ReconnectComplete(object sender, EventArgs e)
        {
            // ignore callbacks from discarded objects.
            if (!Object.ReferenceEquals(sender, _reconnectHandler))
            {
                return;
            }

            _session = _reconnectHandler.Session;
            _reconnectHandler.Dispose();
            _reconnectHandler = null;

            Console.WriteLine("--- RECONNECTED ---");
        }
    }
}

