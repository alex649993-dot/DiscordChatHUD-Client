using System.Net;
namespace DiscordChatHUD.Services;
internal sealed class RecoveryManifestMissingException : InvalidOperationException
{internal RecoveryManifestMissingException() : base("현재 버전의 자동 복구 안내가 없습니다.") { }}
internal static class RecoveryReleaseFallback
{
 internal static void ValidateLatest(UpdateManifest manifest,int installed)
 {
  if(manifest.Version < installed)throw new InvalidDataException("최신 복구 패키지가 설치 버전보다 오래되었습니다.");
  if(manifest.Files is null)throw new InvalidDataException("복구 파일별 검증 정보가 없습니다.");
 }
 internal static async Task<((UpdateManifest Manifest,byte[] Envelope) Update,string Folder)> Download(
     int installed,(UpdateManifest Manifest,byte[] Envelope) update,
     Func<Task<(UpdateManifest Manifest,byte[] Envelope)>> latest,
     Func<UpdateManifest,byte[],Task<string>> download)
 {
  ValidateLatest(update.Manifest,installed);
  try{return(update,await download(update.Manifest,update.Envelope));}
  catch(HttpRequestException ex) when(ex.StatusCode==HttpStatusCode.NotFound && update.Manifest.Version==installed)
  {
   var next=await latest();ValidateLatest(next.Manifest,installed);
   if(next.Manifest.Version<=installed)throw new InvalidOperationException("복구 ZIP이 없고 더 새로운 정식 버전도 없습니다.");
   return(next,await download(next.Manifest,next.Envelope));
  }
 }
}
