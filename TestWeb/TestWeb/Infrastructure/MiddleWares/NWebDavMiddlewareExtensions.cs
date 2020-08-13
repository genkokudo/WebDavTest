
using TestWeb.Infrastructure.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NWebDav.Server;
using NWebDav.Server.AspNetCore;
using NWebDav.Server.Stores;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;

namespace TestWeb.Infrastructure.MiddleWares
{
    /// <summary>
    /// UseNWebDav時のオプション
    /// </summary>
    public class NWebDavOption
    {
        /// <summary>
        /// このパスをWebDAVのホームディレクトリにし、このディレクトリ以下をWebDAVでアクセス可能にする。
        /// Azureだとwwwroot以下しかWebDAVにできないので以下のような設定が良い。
        /// "/wwwroot/upload"
        /// このようにすると"https://localhost:XXXXX/wwwroot/upload"でネットワークの場所の追加をしてアクセスできるようになる。
        /// 
        /// 設定しない場合は"wwwroot"以下は全てWebDAVでアクセス可能になる（良くないので本番化前に必ず設定すること）
        /// </summary>
        public string HomeDirectory { get; set; }

        /// <summary>
        /// WebDAV最大送信サイズ
        /// デフォルト:524288000(500MB)
        /// </summary>
        public long MaxRequestBodySize { get; set; } = 524288000;
    }

    /// <summary>
    /// UseNWebDavをStartup.csで呼べるようにする
    /// </summary>
    public static class NWebDavMiddlewareExtensions
    {
        /// <summary>
        /// NWebDavを用いて、WebDAVクライアントからファイルアクセスできるようにします。
        /// POST後にファイルがCSVならばレポートの作成処理を行います。
        /// </summary>
        /// <param name="builder"></param>
        /// <returns></returns>
        public static IApplicationBuilder UseNWebDav(this IApplicationBuilder builder, NWebDavOption options = null)
        {
            return builder.UseMiddleware<NWebDavMiddleware>();
        }
    }

    /// <summary>
    /// NWebDavを使用します。
    /// NWebDavOptionで設定したHomeDirectoryにPOSTできるようになります。
    /// POST後にファイルがCSVならばレポートの作成処理を行います。
    /// 
    /// テストを行うときは「ネットワークの場所の追加」でHomeDirectoryのアドレスを指定します。
    /// 例：https://localhost:44377/wwwroot/upload
    /// </summary>
    public class NWebDavMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger _logger;
        private readonly NWebDavOption _options;
        private readonly IWebDavService _webDavService;

        public NWebDavMiddleware(RequestDelegate next, ILogger<NWebDavMiddleware> logger, IOptions<NWebDavOption> options, IWebDavService webDavService)
        {
            _next = next;
            _logger = logger;
            _options = options.Value;
            _webDavService = webDavService;
        }

        [DisableRequestSizeLimit]
        public async Task InvokeAsync(HttpContext context)
        {
            try
            {
                #region NWebDav.Server.AspNetCoreについて
                // ファイルアップロードは「参考」にあるようにPUTでおこなわれる。
                // なのでPUTされたタイミングでアップロード処理をしたい。
                // NWebDavにはPUTのときにコールバックされるような仕組みはないが、すべての呼び出しのときにこの下の方にあるapp.Run()が呼び出される。
                // なので、ここでPUTかどうかを判定して独自の処理をおこなえばよい。
                //
                // 参考
                // Windows Explorerでファイルを新規にアップロードすると以下のように呼ばれる（更新や他のツールでアップロードすると呼び出し方は変わる）
                //     PROPFIND
                //     PROPFIND
                //     PROPFIND
                //     PUT
                //     LOCK
                //     PROPPATCH
                //     PROPFIND
                //     HEAD
                //     PUT
                //     PROPPATCH
                //     UNLOCK
                // PROPFINDやPROPPATCHで何を取得しているのかは調べていない
                // 1回目のPUTは0バイトのファイル作成（Content-Lengthも0だしRequest.Streamも空になっている）
                // 0バイトで作ってLOCKしている
                // 2回目のPUTが中身のあるファイル作成
                // 最後にUNLOCKしている
                // （大きなファイルをアップロードするときはアップロード中に他から上書きされないようにロックしたい。
                // 　しかし、PUT兼LOCKがないのでまず0バイトでファイル名だけ確保してロック、ロックできたら中身を
                // 　送信してファイルとして完成させる。ということをするためにこうしているんだと思う）
                #endregion
                bool isCompanyMode = false;     // Program.csのコメントアウトを解除すること、トップページのメッセージを変更すること

                if (context.Request.Path.StartsWithSegments(_options.HomeDirectory))
                {
                    // リクエストのBasic認証情報から入力されたユーザ名とパスワードを取得
                    var input = _webDavService.GetUserInputFromBasic(context);
                    if (input == null)
                    {
                        // Basic認証で取得できない場合、一旦失敗にしてBasic認証ダイアログを表示する
                        // ※一度Basic認証をしてしまうとWindows再起動しないとキャッシュが消えないので、テストする際は注意
                        // 「ネットワークの場所を追加する」ではなく「ネットワークドライブの割り当て」だと資格情報の再入力ができる
                        _logger.LogInformation($"Basic認証に必要な情報を受信出来なかったか認証に失敗したため、入力ダイアログを表示します");
                        _webDavService.Failure(context);
                        return;
                    }

                    //// ユーザ名から会社情報を取得する
                    //// DBから会社ディレクトリを問い合わせる
                    //var user = db.Users.Include(x => x.Company).Include(x => x.UserAuthorities).FirstOrDefault(x => x.Name == input.Name);

                    //if (user == null)
                    //{
                    //    _logger.LogInformation($"ユーザが存在しない:{input.Name}");
                    //    _webDavService.Failure(context);
                    //    return;
                    //}
                    //else if (!_passwordService.VerifyPassword(user.PassWord, input.Password, user.Salt))
                    //{
                    //    _logger.LogInformation($"パスワード照合失敗:{input.Name}");
                    //    _webDavService.Failure(context);
                    //    return;
                    //}
                    //else if (isCompanyMode && string.IsNullOrWhiteSpace(user.Company.WebDavPath))    // wwwroot/upload/会社のWebDav名
                    //{
                    //    _logger.LogInformation($"会社のWebDavパスが設定されていない:{input.Name}:{user.Company.Name}");
                    //    _webDavService.Failure(context);
                    //    return;
                    //}
                    //else // "/wwwroot/upload/xxxx/aaaa.txt" or "/wwwroot/upload/xxxx"
                    //{
                        var webDavPath = string.Empty;
                        //if (isCompanyMode)
                        //{
                        //    webDavPath = user.Company.WebDavPath;
                        //}

                        var companyDirectry = Path.Combine(_options.HomeDirectory, webDavPath).Replace('\\', '/');

                        _logger.LogInformation($"ユーザ照合:{input.Name} Request:{context.Request.Path} HomeDirectory:{_options.HomeDirectory} segmant:{companyDirectry}");
                        if (context.Request.Path.StartsWithSegments(companyDirectry) || context.Request.Path == _options.HomeDirectory)
                        {
                            //// Pathが_options.HomeDirectory、または会社ディレクトリの時
                            //// 権限チェックを行う
                            //var authVrr = user.UserAuthorities.FirstOrDefault(x => x.Authority == Entities.UserAuthority.Vrr);
                            //if (authVrr == null)
                            //{
                            //    // 権限がない
                            //    _logger.LogInformation($"WebDAV送信権限無し:{user.Name}");
                            //    _webDavService.Failure(context);
                            //    return;
                            //}

                            // これを実行すると、WebDav接続が確立する
                            await _webDavService.DispatchWebDavAsync(context, _options.MaxRequestBodySize);

                            // ここからPUTのときの処理
                            var aspNetCoreContext = new AspNetCoreContext(context);
                            if (aspNetCoreContext.Request.HttpMethod == "PUT")
                            {
                                // 対象のフルパスを取得する
                                var fullPath = _webDavService.GetFullPath(context);

                                if (new FileInfo(fullPath).Length != 0)
                                {
                                    // Windows ExplorerはまずPUTで0バイトのファイルを作ってから2度目のPUTで中身をアップロードしてくる
                                    // なので0バイトのときは何もしない

                                    //// CSV読み込みモジュールを使用してDBにCSV情報を登録する
                                    //_logger.LogInformation($"CSVファイル登録処理:{fullPath}");
                                    //context.Response.StatusCode = await csvRegister.RegisterCsv(context.Request, fullPath);

                                    // アクセス日時を現在に設定
                                    var now = DateTime.UtcNow;
                                    File.SetLastAccessTimeUtc(fullPath, now);

                                    // 前日までのファイルを削除
                                    // 対象ファイル以外削除だと複数人が同時に送ったときトラブルになるため
                                    var directoryName = Path.GetDirectoryName(fullPath);
                                    var filename = Path.GetFileName(fullPath);
                                    var files = Directory.GetFiles(directoryName, "*");
                                    foreach (var file in files)
                                    {
                                        if (Path.GetFileName(file) != filename)
                                        {
                                            if (File.GetLastAccessTimeUtc(file) < now.AddDays(-1))
                                            {
                                                File.Delete(file);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        else
                        {
                            _logger.LogInformation($"アドレスが不正:基準パス:{_options.HomeDirectory} リクエストパス:{context.Request.Path} 会社パス:{companyDirectry}");
                            _webDavService.Failure(context);
                            return;
                        }
                    //}

                    // WebDAVディレクトリへのアクセスの場合はnextを呼ばない
                    return;
                }

                // 次のミドルウェアを呼ぶ。これを書かないと次のapp.Useしたミドルウェアが呼ばれないので必ず書くこと。
                await _next(context);

            }
            catch (Exception e)
            {
                if (!e.Message.Contains("SPA"))
                {
                    _logger.LogError(e.ToString());
                }
            }
        }
    }
}
