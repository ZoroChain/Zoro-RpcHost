using Microsoft.Extensions.Configuration;
using System.Reflection;
using System.Net;

namespace Zoro.RpcHost
{
    internal class Settings
    {
        public IPAddress BindAddress { get; }
        public ushort Port { get; }
        public string SslCert { get; }
        public string SslCertPassword { get; }
        public IPAddress AgentAddress { get; }
        public int AgentPort { get; }

        public static Settings Default { get; }

        static Settings()
        {
            IConfigurationSection section = new ConfigurationBuilder().AddJsonFile("config.json").Build().GetSection("ApplicationConfiguration");
            Default = new Settings(section);
        }

        public Settings(IConfigurationSection section)
        {
            this.BindAddress = IPAddress.Parse(section.GetSection("BindAddress").Value);
            this.Port = ushort.Parse(section.GetSection("Port").Value);
            this.SslCert = section.GetSection("SslCert").Value;
            this.SslCertPassword = section.GetSection("SslCertPassword").Value;
            this.AgentAddress = IPAddress.Parse(section.GetSection("AgentAddress").Value);
            this.AgentPort = ushort.Parse(section.GetSection("AgentPort").Value);
        }
    }
}
