using System;
using System.Security.Cryptography;
using System.Text;
namespace ValleBrote.V2
{
    [Serializable] public sealed class UpdateRelease
    {
        public string version,notes,packageUrl,sha256,signature;
        public long bytes;
        public string updaterUrl,updaterSha256,updaterSignature;
        public long updaterBytes;
        public string Payload(){return version+"\n"+packageUrl+"\n"+sha256+"\n"+bytes+"\n"+notes;}
        public bool Verify(string publicKey)
        {
            try{
                Version parsed;Uri uri;
                if(!Version.TryParse(version,out parsed)||!Uri.TryCreate(packageUrl,UriKind.Absolute,out uri)||uri.Scheme!="https"||uri.Host!="github.com"||!uri.AbsolutePath.Contains("/releases/download/")||bytes<=0||bytes>2147483648L||sha256==null||sha256.Length!=64||notes==null||notes.Length>8000)return false;
                foreach(char c in sha256)if(!Uri.IsHexDigit(c))return false;
                using(var rsa=new RSACryptoServiceProvider()){rsa.FromXmlString(publicKey);return rsa.VerifyData(Encoding.UTF8.GetBytes(Payload()),CryptoConfig.MapNameToOID("SHA256"),Convert.FromBase64String(signature));}
            }catch{return false;}
        }
        public bool NewerThan(string current){Version a,b;return Version.TryParse(version,out a)&&Version.TryParse(current,out b)&&a>b;}
        public string UpdaterPayload(){return "ValleBroteUpdater\n"+version+"\n"+updaterUrl+"\n"+updaterSha256+"\n"+updaterBytes;}
        public bool VerifyUpdater(string publicKey)
        {
            try{Uri uri;if(!Uri.TryCreate(updaterUrl,UriKind.Absolute,out uri)||uri.Scheme!="https"||uri.Host!="github.com"||!uri.AbsolutePath.Contains("/releases/download/")||updaterBytes<=0||updaterBytes>10485760||updaterSha256==null||updaterSha256.Length!=64)return false;foreach(char c in updaterSha256)if(!Uri.IsHexDigit(c))return false;
                using(var rsa=new RSACryptoServiceProvider()){rsa.FromXmlString(publicKey);return rsa.VerifyData(Encoding.UTF8.GetBytes(UpdaterPayload()),CryptoConfig.MapNameToOID("SHA256"),Convert.FromBase64String(updaterSignature));}
            }catch{return false;}
        }
    }
    [Serializable] public sealed class UpdateSettings { public string repository="",publicKey=""; }
    [Serializable] public sealed class UpdatePlan
    {
        public UpdateRelease release;
        public string publicKey,installDirectory,executable;
        public int processId;
        public long processStarted;
        public bool allowFreshInstall;
        public string saveBackupPath;
    }
}
