using System; using System.IO; using SharpImage.Formats; using TUnit.Core;
namespace SharpImage.Tests.Formats;
public class Av1TenBitProbe { const string Dir=@"C:\Users\adamm\AppData\Local\Temp\claude\F--Code-QuickFixMyPics2\6657081e-026c-4ec2-a3b0-5a927b1d6dfe\scratchpad";
 [Test] public void Probe(){
   var f = HeifCoder.Decode(File.ReadAllBytes(Path.Combine(Dir, Environment.GetEnvironmentVariable("AV1_FILE")??"t10.avif")));
   var err = typeof(SharpImage.Formats.Av1.Av1Decoder).GetField("LastDecodeError", System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)?.GetValue(null);
   File.WriteAllText(Path.Combine(Dir,"probe_diag.txt"), $"[PROBE] decoded {f.Columns}x{f.Rows} LastDecodeError={err}");
 } }
