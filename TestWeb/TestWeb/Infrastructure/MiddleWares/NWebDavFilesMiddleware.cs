
using TestWeb.Infrastructure.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NWebDav.Server.AspNetCore;

namespace TestWeb.Infrastructure.MiddleWares
{
    /// <summary>
    /// UseNWebDavFiles時のオプション
    /// </summary>
    public class NWebDavFilesOption
    {
        /// <summary>
        /// このパスをWebDAVのホームディレクトリにし、このディレクトリ以下をWebDAVでアクセス可能にする。
        /// Azureだとwwwroot以下しかWebDAVにできないので以下のような設定が良い。
        /// "/wwwroot/files"
        /// このようにすると"https://localhost:XXXXX/wwwroot/files/会社ごとの文字列"でネットワークの場所の追加をしてアクセスできるようになる。
        /// "会社ごとの文字列"は会社マスタで設定する。
        /// 
        /// IISで外部からのアクセスを行えるようにするには、フォルダの書き込み権限を"IIS_IUSRS"ユーザに設定する必要がある
        /// </summary>
        public string HomeDirectory { get; set; }

        /// <summary>
        /// WebDAV最大送信サイズ
        /// デフォルト:524288000(500MB)
        /// </summary>
        public long MaxRequestBodySize { get; set; } = 524288000;
    }

    /// <summary>
    /// UseNWebDavFilesをStartup.csで呼べるようにする
    /// </summary>
    public static class NWebDavFilesMiddlewareExtensions
    {
        /// <summary>
        /// NWebDavを用いて、WebDAVクライアントからファイルアクセスできるようにします。
        /// 企業ごとにアクセスできるディレクトリを作成します。
        /// ディレクトリ名は会社マスタで設定します。
        /// https://ホスト名/wwwroot/files/会社ごとの文字列
        /// </summary>
        /// <param name="builder"></param>
        /// <returns></returns>
        public static IApplicationBuilder UseNWebDavFiles(this IApplicationBuilder builder)
        {
            return builder.UseMiddleware<NWebDavFilesMiddleware>();
        }
    }

    /// <summary>
    /// NWebDavを使用します。
    /// 
    /// </summary>
    public class NWebDavFilesMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger _logger;
        private readonly NWebDavFilesOption _options;
        private readonly IWebDavService _webDavService;

        public NWebDavFilesMiddleware(RequestDelegate next, ILogger<NWebDavFilesMiddleware> logger, IOptions<NWebDavFilesOption> options, IWebDavService webDavService)
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
                // 設定したディレクトリにアクセスがあったときのみ処理
                if (context.Request.Path.StartsWithSegments(Path.Combine(_options.HomeDirectory)))
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

                    // リクエストのBasic認証情報から入力されたユーザ名とパスワードを取得
                    var input = _webDavService.GetUserInputFromBasic(context);

                    if (input == null)
                    {
                        //// Basic認証ではない場合、アドレスからユーザ名とパスワードを取得する
                        //input = GetUserInputFromAddress(context.Request.Path);
                        //if (context.Request.Query.ContainsKey("name"))
                        //{
                        //    // とばされてきた
                        //    input = new UserInput
                        //    {
                        //        Name = context.Request.Query["name"],
                        //        Password = context.Request.Query["password"],
                        //        Path = context.Request.Path
                        //    };
                        //}
                        //else if (input != null)   // 繋がるが、ファイル・フォルダ追加が出来ないので没
                        //{
                        //    // アドレスによる認証の場合、本来アクセスしたいアドレスに直す
                        //    //context.Request.Path = new PathString(input.Path);

                        //    context.Response.Redirect(input.Path + $"?name={input.Name}&password={input.Password}");
                        //    return;
                        //}
                        //else
                        //{

                        // 取得できない場合、一旦失敗にしてBasic認証ダイアログを表示する
                        // ※一度Basic認証をしてしまうとWindows再起動しないとキャッシュが消えないので、テストする際は注意
                        // 「ネットワークの場所を追加する」ではなく「ネットワークドライブの割り当て」だと資格情報の再入力ができる
                        _logger.LogInformation($"Basic認証に必要な情報を受信出来なかったか認証に失敗したため、入力ダイアログを表示します");
                        _webDavService.Failure(context);
                        return;

                        //}
                    }

                    // ユーザ名から会社情報を取得する
                    // DBから会社ディレクトリを問い合わせる
                    var webDavPath = "test";

                    var companyDirectry = Path.Combine(_options.HomeDirectory, webDavPath).Replace('\\', '/');

                    _logger.LogInformation($"Basic認証ユーザ名:{input.Name} Basic認証ユーザの会社パス:{companyDirectry} Request:{context.Request.Path}");
                    if (context.Request.Path.StartsWithSegments(companyDirectry) || context.Request.Path == _options.HomeDirectory)
                    {
                        // Pathが_options.HomeDirectory、または会社ディレクトリの時
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

                                // ※ファイルに対して何か処理する場合はここに書く
                                _logger.LogInformation($"ファイルのPUT処理:{fullPath}");
                            }
                        }
                    }
                    else
                    {
                        _logger.LogInformation($"アドレスが不正:基準パス:{_options.HomeDirectory} リクエストパス:{context.Request.Path} 会社パス:{companyDirectry}");
                        _webDavService.Failure(context);
                        return;
                    }

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

        // 繋がるが、ファイル・フォルダ追加が出来ないので没
        ///// <summary>
        ///// アドレスからユーザ名とパスワードを取得する
        ///// </summary>
        ///// <param name="address">アドレス</param>
        ///// <returns>ユーザ名とパスワード、取れなければnull</returns>
        //private UserInput GetUserInputFromAddress(string address)
        //{
        //    _logger.LogDebug($"GetUserInfoFromAddress:{address}");
        //    try
        //    {
        //        var separator = $"/{UserInput.Drive}/";
        //        if (address.Contains(separator))
        //        {
        //            var splited = address.Split(separator)[1].Split('/');
        //            var result = new UserInput
        //            {
        //                Name = splited[0],
        //                Password = splited[1],
        //                Path = address.Replace($"/{UserInput.Drive}/{splited[0]}/{splited[1]}", string.Empty)
        //            };
        //            _logger.LogInformation($"アドレスによる認証 user:{result.Name}");
        //            return result;
        //        }
        //    }
        //    catch (Exception)
        //    {
        //        _logger.LogDebug($"アドレスから情報が取得できません:{address}");
        //    }

        //    // 取得失敗した場合null
        //    return null;
        //}
    }
}
