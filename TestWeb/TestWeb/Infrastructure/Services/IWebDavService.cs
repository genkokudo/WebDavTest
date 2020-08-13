using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using NWebDav.Server;
using NWebDav.Server.AspNetCore;
using NWebDav.Server.Stores;
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace TestWeb.Infrastructure.Services
{
    /// <summary>
    /// WebDav関係で使用するサービス
    /// </summary>
    public interface IWebDavService
    {
        /// <summary>
        /// コンテキストから対象のフルパスを取得する
        /// </summary>
        /// <param name="context"></param>
        /// <returns></returns>
        public string GetFullPath(HttpContext context);

        /// <summary>
        /// 同期IOの許可設定
        /// これを実行すると、WebDav接続が確立する
        /// </summary>
        /// <param name="context">リクエスト、ここのPathにアクセス許可する</param>
        /// <param name="maxRequestBodySize">ファイルサイズ上限設定</param>
        public Task DispatchWebDavAsync(HttpContext context, long maxRequestBodySize);

        /// <summary>
        /// ヘッダからBasic認証によって入力されたユーザ名とパスワードを取得する
        /// </summary>
        /// <param name="context"></param>
        /// <returns>ユーザ名とパスワード、取れなければnull</returns>
        public UserInput GetUserInputFromBasic(HttpContext context);

        /// <summary>
        /// 認証失敗時、Basic認証ダイアログを出す
        /// </summary>
        /// <param name="context"></param>
        public void Failure(HttpContext context);

        /// <summary>
        /// ユーザの入力
        /// </summary>
        public class UserInput
        {
            /// <summary>
            /// 入力ユーザ名
            /// </summary>
            public string Name { get; set; }

            /// <summary>
            /// 入力パスワード
            /// </summary>
            public string Password { get; set; }
        }
    }

    public class WebDavService : IWebDavService
    {
        private readonly IWebHostEnvironment _env;
        private readonly ILogger _logger;

        public WebDavService(IWebHostEnvironment env, ILogger<WebDavService> logger)
        {
            _env = env;
            _logger = logger;
        }

        /// <summary>
        /// コンテキストから対象のフルパスを取得する
        /// </summary>
        /// <param name="context"></param>
        /// <returns></returns>
        public string GetFullPath(HttpContext context)
        {
            var aspNetCoreContext = new AspNetCoreContext(context);
            var splitUri = NWebDav.Server.Helpers.RequestHelper.SplitUri(aspNetCoreContext.Request.Url);
            var requestedPath = NWebDav.Server.Helpers.UriHelper.GetDecodedPath(splitUri.CollectionUri).Substring(1).Replace('/', Path.DirectorySeparatorChar);
            return Path.GetFullPath(Path.Combine(_env.ContentRootPath, requestedPath, splitUri.Name));
        }

        /// <summary>
        /// 同期IOの許可設定
        /// これを実行すると、WebDav接続が確立する
        /// </summary>
        /// <param name="context">リクエスト、ここのPathにアクセス許可する</param>
        /// <param name="maxRequestBodySize">ファイルサイズ上限設定</param>
        public async Task DispatchWebDavAsync(HttpContext context, long maxRequestBodySize)
        {
            // WebDAVのホームディレクトリはルート（env.ContentRootPath）にする。
            // example.com/Folder をWebDAVフォルダーにする場合 Path.Combine(env.ContentRootPath, "Folder") に設定するので注意
            var diskStore = new DiskStore(_env.ContentRootPath);
            var webDavDispatcher = new WebDavDispatcher(diskStore, new RequestHandlerFactory());

            #region ★重要★
            // 「HTTP:すべてのサーバーで同期 IO が無効になっています」
            // https://docs.microsoft.com/ja-jp/dotnet/core/compatibility/2.2-3.0?ranMID=24542&ranEAID=je6NUbpObpQ&ranSiteID=je6NUbpObpQ-iZfCg91g.AS8jvrKtk4V9A&epi=je6NUbpObpQ-iZfCg91g.AS8jvrKtk4V9A&irgwc=1&OCID=AID2000142_aff_7593_1243925&tduid=(ir__efbyncee3skftyzckk0sohzx0u2xlq6ldx6jeoyv00)(7593)(1243925)(je6NUbpObpQ-iZfCg91g.AS8jvrKtk4V9A)()&irclickid=_efbyncee3skftyzckk0sohzx0u2xlq6ldx6jeoyv00#http-synchronous-io-disabled-in-all-servers
            // 
            // 上記のようにASP.NET Core 3.0では同期IOが無効になっている。
            // NWebDav.Server.AspNetCoreは内部でXDocument.Load()を使っているヶ所がある。
            // これも同期IOなのでデフォルトのままだと例外が発生する。
            // なのでmsdocsにあるように以下のようにNWebDavのときは同期IOを許可するようにしてやる必要がある。
            // 
            // ちなみに、XDocument.Load()を呼び出してるのはLockHandlerクラスとPropPatchHandlerクラスから
            // 呼び出しているRequestHelper.LoadXmlDocument()の中（他にもあるかもしれないが未確認）。
            // なのでWindows Explorerからファイルをアップロードすると0バイトのファイルを作って
            // その後のLOCKで例外でエラーが返るので0バイトのファイルができたところで終わってしまう。
            #endregion
            context.Features.Get<IHttpMaxRequestBodySizeFeature>().MaxRequestBodySize = maxRequestBodySize;
            var syncIOFeature = context.Features.Get<IHttpBodyControlFeature>();
            if (syncIOFeature != null)
            {
                syncIOFeature.AllowSynchronousIO = true;
            }
            var httpContext = new AspNetCoreContext(context);
            await webDavDispatcher.DispatchRequestAsync(httpContext).ConfigureAwait(false);
            _logger.LogInformation($"接続完了");
        }

        /// <summary>
        /// ヘッダからBasic認証によって入力されたユーザ名とパスワードを取得する
        /// </summary>
        /// <param name="context"></param>
        /// <returns>ユーザ名とパスワード、取れなければnull</returns>
        public IWebDavService.UserInput GetUserInputFromBasic(HttpContext context)
        {
            _logger.LogDebug($"GetUserInputFromBasic");
            try
            {
                var header = string.Empty;
                header = context.Request.Headers["Authorization"];
                if (header != null && header.StartsWith("Basic"))
                {
                    // VRRからBasic認証を行う場合、またはエクスプローラから認証失敗した後にBasic認証を行う場合
                    // ※https://username:password@example.com のような方法は最新のブラウザではできない

                    // ユーザ名とパスワードを取得
                    var encodedCredentials = header.Substring("Basic".Length).Trim();
                    var credentials = Encoding.UTF8.GetString(Convert.FromBase64String(encodedCredentials));
                    var separatorIndex = credentials.IndexOf(':');

                    var result = new IWebDavService.UserInput
                    {
                        Name = credentials.Substring(0, separatorIndex),
                        Password = credentials.Substring(separatorIndex + 1)
                    };
                    _logger.LogInformation($"Basic認証ヘッダあり user:{result.Name}");
                    return result;
                }
            }
            catch (Exception)
            {
                _logger.LogDebug($"Basic認証の入力情報が取得できません:{context.Request.Path}");
            }

            // 取得失敗した場合null
            return null;
        }

        /// <summary>
        /// 認証失敗時、Basic認証ダイアログを出す
        /// </summary>
        /// <param name="context"></param>
        public void Failure(HttpContext context)
        {
            var httpContext = new AspNetCoreContext(context);
            if (httpContext.Request.HttpMethod == "PUT")
            {
                var splitUri = NWebDav.Server.Helpers.RequestHelper.SplitUri(httpContext.Request.Url);
                var requestedPath = NWebDav.Server.Helpers.UriHelper.GetDecodedPath(splitUri.CollectionUri).Substring(1).Replace('/', Path.DirectorySeparatorChar);
                var fullPath = Path.GetFullPath(Path.Combine(_env.ContentRootPath, requestedPath, splitUri.Name));
                _logger.LogInformation($"PUT失敗:{fullPath}");
                // 認証失敗時は削除
                File.Delete(fullPath);
            }
            context.Response.Headers["WWW-Authenticate"] = "Basic"; // ブラウザの認証ダイアログを出す
            context.Response.StatusCode = 401;
        }
    }
}
