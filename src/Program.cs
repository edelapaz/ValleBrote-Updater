using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using ValleBrote.V2;

static class Program
{
    static string work;
    static Action<string> beforeReplaceTestHook;
    static UpdateSettings PinnedSettings(){using(var stream=System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("ValleBrote.UpdateSettings"))using(var reader=new StreamReader(stream))return new JavaScriptSerializer().Deserialize<UpdateSettings>(reader.ReadToEnd());}
    static bool ValidExecutable(string name)
    {
        return !string.IsNullOrEmpty(name)&&string.Equals(name,"ValleBrote.exe",StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(name)==name;
    }
    static string Hash(string path){using(var stream=File.OpenRead(path))using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-","").ToLowerInvariant();}
    static string Within(string root,string relative)
    {
        if(string.IsNullOrEmpty(relative)||Path.IsPathRooted(relative)||relative.Contains(":"))throw new InvalidDataException("Ruta de paquete inválida.");
        string prefix=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        string path=Path.GetFullPath(Path.Combine(prefix,relative.Replace('/',Path.DirectorySeparatorChar)));
        if(!path.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Ruta fuera del juego.");
        string cursor=Path.GetDirectoryName(path);
        while(cursor!=null&&cursor.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)){
            if(Directory.Exists(cursor)&&(File.GetAttributes(cursor)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("No se admiten enlaces de carpetas.");cursor=Path.GetDirectoryName(cursor);
        }
        if(File.Exists(path)&&(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("No se admiten enlaces de archivos.");
        return path;
    }
    static List<string> Extract(string zipPath,string stage,string executable)
    {
        var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var files=new List<string>();long total=0;
        using(var zip=ZipFile.OpenRead(zipPath)){
            if(zip.Entries.Count>20000)throw new InvalidDataException("Demasiados archivos.");
            foreach(var entry in zip.Entries){
                string destination=Within(stage,entry.FullName);
                if(!names.Add(destination))throw new InvalidDataException("Archivo duplicado.");
                if((entry.ExternalAttributes>>16&0xF000)==0xA000)throw new InvalidDataException("Enlace no permitido.");
                total+=entry.Length;if(total>4294967296L)throw new InvalidDataException("Paquete demasiado grande.");
                if(entry.FullName.EndsWith("/")){Directory.CreateDirectory(destination);continue;}
                Directory.CreateDirectory(Path.GetDirectoryName(destination));entry.ExtractToFile(destination);files.Add(entry.FullName);
            }
        }
        if(!File.Exists(Within(stage,executable))||!File.Exists(Within(stage,"UnityPlayer.dll"))||!Directory.Exists(Path.Combine(stage,Path.GetFileNameWithoutExtension(executable)+"_Data")))throw new InvalidDataException("El paquete no contiene un juego completo.");
        if(!File.Exists(Within(stage,"dstorage.dll"))||!File.Exists(Within(stage,"dstoragecore.dll")))throw new InvalidDataException("El paquete no contiene las bibliotecas DirectStorage requeridas.");
        return files;
    }
    static void Apply(string stage,string install,List<string> files,string backup,int failAfter)
    {
        // Check every target and every parent BEFORE replacing the first game file.
        var checkedDirectories=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(string name in files){
            string target=Within(install,name),directory=Path.GetDirectoryName(target);Directory.CreateDirectory(directory);
            if(checkedDirectories.Add(directory)){
                string probe=Path.Combine(directory,"vallebrote-write-check-"+Guid.NewGuid().ToString("N")+".tmp");
                using(var test=new FileStream(probe,FileMode.CreateNew,FileAccess.Write,FileShare.None)){test.WriteByte(1);test.Flush(true);}File.Delete(probe);
            }
            if(File.Exists(target)){
                if((File.GetAttributes(target)&FileAttributes.ReadOnly)!=0)throw new IOException("La instalación contiene un archivo de solo lectura: "+name+". No se modificó ningún archivo del juego. No cambies permisos de Windows; usa el reparador en una carpeta de tu usuario.");
                RetryFile(()=>{using(var check=new FileStream(target,FileMode.Open,FileAccess.ReadWrite,FileShare.None)){}},name);
            }
        }
        foreach(string name in files){string target=Within(install,name);if(File.Exists(target)){string copy=Within(backup,name);Directory.CreateDirectory(Path.GetDirectoryName(copy));File.Copy(target,copy,true);if(Hash(copy)!=Hash(target))throw new IOException("La copia de seguridad no coincide: "+name);}}
        var changed=new List<string>();
        string inProgress=null;
        try{
            foreach(string name in files){
                inProgress=name;if(beforeReplaceTestHook!=null)beforeReplaceTestHook(name);AtomicCopy(Within(stage,name),Within(install,name));changed.Add(name);inProgress=null;
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(backup),"transaction-files.json"),new JavaScriptSerializer().Serialize(changed));
                if(failAfter>0&&changed.Count==failAfter)throw new IOException("Fallo de prueba.");
            }
        }catch(Exception original){
            // Do not try restoring a failed copy that never changed its original target.
            if(inProgress!=null){string target=Within(install,inProgress),copy=Within(backup,inProgress);try{if(File.Exists(copy)?!File.Exists(target)||Hash(target)!=Hash(copy):File.Exists(target))changed.Add(inProgress);}catch{}}
            var errors=new List<Exception>();
            for(int i=changed.Count-1;i>=0;i--)try{string target=Within(install,changed[i]),copy=Within(backup,changed[i]);if(File.Exists(copy))AtomicCopy(copy,target);else if(File.Exists(target))RetryFile(()=>File.Delete(target),changed[i]);}catch(Exception e){errors.Add(e);}
            if(errors.Count>0){errors.Insert(0,original);throw new AggregateException("La actualización se detuvo: "+original.Message+". Algunos archivos no pudieron restaurarse. Tu guardado no fue modificado. Conserva la copia en "+backup+" y utiliza el reparador en una carpeta nueva de tu usuario.",errors);}
            throw new IOException("La actualización se detuvo y se restauraron los archivos anteriores. Motivo: "+original.Message,original);
        }
    }
    static void RetryFile(Action action,string name)
    {
        Exception last=null;
        for(int attempt=0;attempt<12;attempt++){
            try{action();return;}catch(IOException e){last=e;}catch(UnauthorizedAccessException e){last=e;}
            if(attempt<11)System.Threading.Thread.Sleep(250);
        }
        throw new IOException("Windows no permite reemplazar '"+name+"'. Cierra todas las ventanas del juego y vuelve a intentar. No se cambiaron permisos ni protecciones de Windows. "+last.Message,last);
    }
    static void AtomicCopy(string source,string target)
    {
        // The temporary file is beside the target, so replacement stays on the same volume.
        string temporary=Path.Combine(Path.GetDirectoryName(target),"vallebrote-replace-"+Guid.NewGuid().ToString("N")+".tmp");
        try{
            using(var input=File.OpenRead(source))using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){input.CopyTo(output);output.Flush(true);}
            if(Hash(source)!=Hash(temporary))throw new IOException("La copia temporal no coincide: "+Path.GetFileName(target));
            RetryFile(()=>{if(File.Exists(target))File.Replace(temporary,target,null);else File.Move(temporary,target);},Path.GetFileName(target));
        }finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    static void Install(UpdatePlan plan,Action<string> status)
    {
        var pinned=PinnedSettings();
        if(plan==null||plan.release==null||!plan.release.Verify(pinned.publicKey)||!new Uri(plan.release.packageUrl).AbsolutePath.StartsWith("/"+pinned.repository+"/releases/download/",StringComparison.Ordinal))throw new InvalidDataException("La firma o el origen de la actualización no es válido.");
        string install=Path.GetFullPath(plan.installDirectory);
        if(!Directory.Exists(install)||(File.GetAttributes(install)&FileAttributes.ReparsePoint)!=0||!ValidExecutable(plan.executable))throw new InvalidDataException("Destino inválido.");
        string exe=Within(install,plan.executable);if(!File.Exists(exe)&&!plan.allowFreshInstall)throw new FileNotFoundException("No se encontró el juego.");
        status("Esperando a que se cierre el juego…");
        Process process=null;if(plan.processId>0)try{process=Process.GetProcessById(plan.processId);}catch(ArgumentException){}
        if(process!=null){using(process){if(process.StartTime.ToUniversalTime().Ticks!=plan.processStarted||!string.Equals(process.MainModule.FileName,exe,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("El proceso no corresponde al juego.");if(!process.WaitForExit(120000))throw new IOException("El juego sigue abierto. Ciérralo y vuelve a intentar.");}}
        foreach(var other in Process.GetProcessesByName("ValleBrote")){using(other){try{if(!other.HasExited&&(plan.allowFreshInstall||string.Equals(other.MainModule.FileName,exe,StringComparison.OrdinalIgnoreCase)))throw new IOException("Hay otra ventana del juego usando esta instalación. Ciérrala antes de actualizar; no se modificó ningún archivo.");}catch(InvalidOperationException){}}}
        string probe=Path.Combine(install,"update-check-"+Guid.NewGuid().ToString("N")+".tmp");File.WriteAllText(probe,"ValleBrote update permission check");File.Delete(probe);
        status("Descargando la versión "+plan.release.version+"…");
        ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;
        string zipPath=Path.Combine(work,"package.zip");
        var request=(HttpWebRequest)WebRequest.Create(plan.release.packageUrl);request.UserAgent="ValleBroteUpdater/1.0";request.Timeout=15000;request.ReadWriteTimeout=30000;
        using(var response=request.GetResponse())using(var input=response.GetResponseStream())using(var output=File.Create(zipPath)){
            byte[] buffer=new byte[65536];int count;long downloaded=0;var watch=Stopwatch.StartNew();
            while((count=input.Read(buffer,0,buffer.Length))>0){downloaded+=count;if(downloaded>plan.release.bytes||watch.Elapsed.TotalMinutes>15)throw new InvalidDataException("Descarga fuera de los límites esperados.");output.Write(buffer,0,count);}
        }
        if(new FileInfo(zipPath).Length!=plan.release.bytes||!string.Equals(Hash(zipPath),plan.release.sha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("La descarga no pasó la comprobación de integridad.");
        status("Verificando e instalando. Conservando copia de seguridad…");
        string stage=Path.Combine(work,"stage"),backup=Path.Combine(work,"backup");
        var files=Extract(zipPath,stage,plan.executable);Apply(stage,install,files,backup,0);
        File.WriteAllText(Path.Combine(work,"installed.txt"),plan.release.version);
        status("Actualización completada. Abriendo Valle Brote…");
        Process.Start(new ProcessStartInfo(exe){WorkingDirectory=install,UseShellExecute=true});
    }
    [STAThread] static int Main(string[] args)
    {
        if(args.Length==1&&args[0]=="--self-test")return SelfTest();
        if(args.Length==5&&args[0]=="--verify-upgrade")return VerifyUpgrade(args[1],args[2],args[3],args[4]);
        Application.EnableVisualStyles();
        var form=new Form{Text="Actualización de Valle Brote",Width=540,Height=190,StartPosition=FormStartPosition.CenterScreen,ControlBox=false};
        var label=new Label{Left=22,Top=28,Width=480,Height=90,Text="Preparando actualización…"};form.Controls.Add(label);
        int code=0;
        form.Shown+=async delegate{
            try{
                UpdatePlan plan;
                if(args.Length==0||args.Length==1&&args[0]=="--repair"){
                    string destination=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","ValleBrote");
                    if(MessageBox.Show("Se descargará la versión firmada más reciente y se instalará una copia completa en:\n\n"+destination+"\n\nNo se eliminará la instalación anterior ni se modificará tu guardado. No se cambiarán permisos ni protecciones de Windows.\n\n¿Quieres continuar?","Reparar Valle Brote",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes){form.Close();return;}
                    work=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ValleBrote","Updates","repair-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(work);
                    label.Text="Consultando la versión firmada…";
                    plan=await System.Threading.Tasks.Task.Run(()=>CreateRepairPlan(destination));
                    File.WriteAllText(Path.Combine(work,"plan.json"),new JavaScriptSerializer().Serialize(plan));
                }else{
                    if(args.Length!=1)throw new ArgumentException("Falta el plan de actualización.");
                    string path=Path.GetFullPath(args[0]);work=Path.GetDirectoryName(path);plan=new JavaScriptSerializer().Deserialize<UpdatePlan>(File.ReadAllText(path));
                }
                await System.Threading.Tasks.Task.Run(()=>Install(plan,text=>form.BeginInvoke((Action)(()=>label.Text=text))));
            }catch(Exception error){code=1;if(work!=null)File.WriteAllText(Path.Combine(work,"error.txt"),error.ToString());MessageBox.Show("No se completó la actualización. El actualizador no modifica los guardados.\n\n"+error.Message+"\n\nSi la instalación anterior está dañada, ejecuta este reparador sin argumentos para crear una copia completa en tu carpeta de usuario. Conserva la carpeta backup.","Valle Brote",MessageBoxButtons.OK,MessageBoxIcon.Error);}
            form.Close();
        };
        Application.Run(form);return code;
    }
    static void Assert(bool condition,string text){if(!condition)throw new Exception(text);Console.WriteLine("PASS "+text);}
    static UpdatePlan CreateRepairPlan(string destination)
    {
        var settings=PinnedSettings();ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;
        var request=(HttpWebRequest)WebRequest.Create("https://github.com/"+settings.repository+"/releases/latest/download/latest.json");request.Timeout=15000;request.ReadWriteTimeout=15000;request.UserAgent="ValleBroteRepair/1.0";
        string json;using(var response=request.GetResponse())using(var reader=new StreamReader(response.GetResponseStream())){char[] buffer=new char[32769];int read=reader.ReadBlock(buffer,0,buffer.Length);if(read>32768)throw new InvalidDataException("Manifiesto demasiado grande.");json=new string(buffer,0,read);}
        var release=new JavaScriptSerializer().Deserialize<UpdateRelease>(json);if(release==null||!release.Verify(settings.publicKey))throw new InvalidDataException("No se pudo verificar la versión publicada.");
        Directory.CreateDirectory(destination);
        return new UpdatePlan{release=release,publicKey=settings.publicKey,installDirectory=destination,executable="ValleBrote.exe",processId=0,allowFreshInstall=true};
    }
    static int VerifyUpgrade(string oldZip,string newZip,string manifestPath,string reportPath)
    {
        try{
            var release=new JavaScriptSerializer().Deserialize<UpdateRelease>(File.ReadAllText(manifestPath));
            if(!release.Verify(PinnedSettings().publicKey)||Hash(newZip)!=release.sha256)throw new InvalidDataException("El paquete de prueba no coincide con la versión firmada.");
            string root=Path.GetFullPath(Path.Combine("Logs","upgrade-tests",Guid.NewGuid().ToString("N")));Directory.CreateDirectory(root);
            string install=Path.Combine(root,"game"),stage=Path.Combine(root,"stage"),backup=Path.Combine(root,"backup");var oldFiles=Extract(oldZip,install,"ValleBrote.exe");var files=Extract(newZip,stage,"ValleBrote.exe");
            var original=new Dictionary<string,string>();foreach(string name in oldFiles)original[name]=Hash(Within(install,name));
            string playerSave=Path.Combine(root,"player-save-probe.json");File.WriteAllText(playerSave,"{\"coins\":987,\"xp\":1234,\"seeds\":81,\"research\":[\"corn\",\"compost\"]}");string saveHash=Hash(playerSave);
            bool failed=false;try{Apply(stage,install,files,Path.Combine(root,"rollback-probe"),3);}catch(IOException){failed=true;}
            if(!failed)throw new Exception("El fallo inyectado no se produjo.");foreach(var entry in original)if(Hash(Within(install,entry.Key))!=entry.Value)throw new Exception("El rollback alteró "+entry.Key);
            Apply(stage,install,files,backup,0);foreach(string name in files)if(Hash(Within(install,name))!=Hash(Within(stage,name)))throw new Exception("La instalación actualizada no coincide: "+name);
            if(Hash(playerSave)!=saveHash)throw new Exception("El guardado de prueba cambió.");
            var start=new ProcessStartInfo(Within(install,"ValleBrote.exe"),"--smoke-test -force-d3d11 -screen-width 1000 -screen-height 700 -screen-fullscreen 0 -logFile \""+Path.Combine(root,"player.log")+"\""){WorkingDirectory=install,UseShellExecute=false,WindowStyle=ProcessWindowStyle.Hidden};
            using(var game=Process.Start(start)){if(!game.WaitForExit(180000)){game.Kill();throw new Exception("La instalación actualizada no completó el diagnóstico.");}if(game.ExitCode!=0)throw new Exception("La instalación actualizada salió con error "+game.ExitCode);}
            string smoke=File.ReadAllText(Path.Combine(install,"smoke-results.txt"));if(!smoke.StartsWith("PASS:")||smoke.Contains("FAIL:"))throw new Exception("Falló el diagnóstico de la instalación actualizada.");
            File.WriteAllText(reportPath,"PASS: signed package updates an existing installation, survives injected rollback, preserves the saved-resource probe and boots the updated game.\nCandidate SHA256: "+release.sha256+"\nBaseline SHA256: "+Hash(oldZip)+"\nFixture: "+root+"\n\n"+smoke);return 0;
        }catch(Exception error){File.WriteAllText(reportPath,"FAIL: "+error);return 1;}
    }
    static int SelfTest()
    {
        try{
            Assert(ValidExecutable("ValleBrote.exe")&&ValidExecutable("vallebrote.exe")&&ValidExecutable("VALLEBROTE.EXE"),"Windows executable casing is accepted");
            Assert(!ValidExecutable(null)&&!ValidExecutable("")&&!ValidExecutable("../ValleBrote.exe")&&!ValidExecutable("C:\\ValleBrote.exe")&&!ValidExecutable("other.exe")&&!ValidExecutable("ValleBrote.exe:stream"),"Executable paths, other programs and alternate streams remain rejected");
            work=Path.GetFullPath(Path.Combine("Logs","updater-tests",Guid.NewGuid().ToString("N")));Directory.CreateDirectory(work);
            string source=Path.Combine(work,"source"),stage=Path.Combine(work,"stage"),install=Path.Combine(work,"game"),zip=Path.Combine(work,"test.zip");
            Directory.CreateDirectory(Path.Combine(source,"ValleBrote_Data"));Directory.CreateDirectory(install);
            File.WriteAllText(Path.Combine(source,"ValleBrote.exe"),"new");File.WriteAllText(Path.Combine(source,"UnityPlayer.dll"),"player");File.WriteAllText(Path.Combine(source,"ValleBrote_Data","test"),"data");
            string incomplete=Path.Combine(work,"incomplete.zip");ZipFile.CreateFromDirectory(source,incomplete);
            bool missingRejected=false;try{Extract(incomplete,Path.Combine(work,"incomplete-stage"),"ValleBrote.exe");}catch(InvalidDataException){missingRejected=true;}
            Assert(missingRejected,"Package missing DirectStorage dependencies is rejected before installation");
            File.WriteAllText(Path.Combine(source,"dstorage.dll"),"storage");File.WriteAllText(Path.Combine(source,"dstoragecore.dll"),"storage core");
            ZipFile.CreateFromDirectory(source,zip);var files=Extract(zip,stage,"ValleBrote.exe");Assert(files.Count==5,"Complete Windows package extracts with DirectStorage");
            File.WriteAllText(Path.Combine(install,"ValleBrote.exe"),"old");File.WriteAllText(Path.Combine(install,"untouched-save"),"save");
            bool failed=false;try{Apply(stage,install,files,Path.Combine(work,"rollback"),2);}catch(IOException){failed=true;}
            Assert(failed&&File.ReadAllText(Path.Combine(install,"ValleBrote.exe"))=="old"&&!File.Exists(Path.Combine(install,"UnityPlayer.dll")),"Failure rolls back both replaced and added files");
            Apply(stage,install,files,Path.Combine(work,"backup"),0);Assert(File.ReadAllText(Path.Combine(install,"ValleBrote.exe"))=="new"&&File.ReadAllText(Path.Combine(install,"untouched-save"))=="save","Update preserves unrelated files");
            File.WriteAllText(Path.Combine(stage,"ValleBrote.exe"),"newer");
            using(var locked=new FileStream(Path.Combine(install,"UnityPlayer.dll"),FileMode.Open,FileAccess.Read,FileShare.None)){
                failed=false;try{Apply(stage,install,files,Path.Combine(work,"locked-backup"),0);}catch(IOException){failed=true;}
                Assert(failed&&File.ReadAllText(Path.Combine(install,"ValleBrote.exe"))=="new","Locked later file aborts before changing the first executable");
            }
            string readOnly=Path.Combine(install,"dstoragecore.dll");var attributes=File.GetAttributes(readOnly);File.SetAttributes(readOnly,attributes|FileAttributes.ReadOnly);
            try{failed=false;try{Apply(stage,install,files,Path.Combine(work,"readonly-backup"),0);}catch(IOException){failed=true;}Assert(failed&&File.ReadAllText(Path.Combine(install,"ValleBrote.exe"))=="new","Read-only later file aborts before any game file changes");}finally{File.SetAttributes(readOnly,attributes);}
            FileStream lateLock=null;
            beforeReplaceTestHook=name=>{if(name=="dstoragecore.dll")lateLock=new FileStream(Within(install,name),FileMode.Open,FileAccess.Read,FileShare.None);};
            try{failed=false;try{Apply(stage,install,files,Path.Combine(work,"late-lock-backup"),0);}catch(IOException){failed=true;}Assert(failed&&File.ReadAllText(Path.Combine(install,"ValleBrote.exe"))=="new"&&File.ReadAllText(Path.Combine(install,"untouched-save"))=="save","Lock during commit restores changed files and skips an untouched locked target");}finally{beforeReplaceTestHook=null;if(lateLock!=null)lateLock.Dispose();}
            string bad=Path.Combine(work,"bad.zip");using(var archive=ZipFile.Open(bad,ZipArchiveMode.Create))using(var writer=new StreamWriter(archive.CreateEntry("../escaped.exe").Open()))writer.Write("bad");
            failed=false;try{Extract(bad,Path.Combine(work,"unsafe"),"ValleBrote.exe");}catch(InvalidDataException){failed=true;}Assert(failed&&!File.Exists(Path.Combine(work,"escaped.exe")),"ZIP traversal is rejected");
            using(var rsa=new RSACryptoServiceProvider(2048)){
                var release=new UpdateRelease{version="0.2.0",notes="Prueba",packageUrl="https://github.com/test/game/releases/download/v0.2.0/game.zip",bytes=new FileInfo(zip).Length,sha256=Hash(zip)};
                release.signature=Convert.ToBase64String(rsa.SignData(Encoding.UTF8.GetBytes(release.Payload()),CryptoConfig.MapNameToOID("SHA256")));
                Assert(release.Verify(rsa.ToXmlString(false))&&release.NewerThan("0.1.0")&&!release.NewerThan("0.2.0"),"Signed manifest and version ordering");
                release.notes="tampered";Assert(!release.Verify(rsa.ToXmlString(false)),"Tampered manifest is rejected");
            }
            string before=Hash(zip);File.AppendAllText(zip,"tamper");Assert(Hash(zip)!=before,"Modified package fails its hash");
            File.WriteAllText("Logs/updater-test-results.txt","13 checks passed\n"+work);return 0;
        }catch(Exception error){Directory.CreateDirectory("Logs");File.WriteAllText("Logs/updater-test-results.txt",error.ToString());return 1;}
    }
}
