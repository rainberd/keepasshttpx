using Griffin.Networking.Buffers;
using Griffin.Networking.Protocol.Http;
using Griffin.Networking.Protocol.Http.Protocol;
using Griffin.Networking.Servers;
using System.IO;
using System.Net;

namespace KeePassHttp
{
    public delegate void RequestHandlerDelegate(IRequest request, IResponse response);

    public class KeePassHttpServiceFactory : IServiceFactory
    {
        private RequestHandlerDelegate RequestHandler;

        public KeePassHttpServiceFactory(RequestHandlerDelegate requestHandler)
        {
            this.RequestHandler = requestHandler;
        }

        public INetworkService CreateClient(EndPoint remoteEndPoint)
        {
            return new KeePassHttpService(RequestHandler);
        }
    }

    public class KeePassHttpService : HttpService
    {
        private static readonly BufferSliceStack Stack = new BufferSliceStack(50, 32000);
        private RequestHandlerDelegate RequestHandler;

        public KeePassHttpService(RequestHandlerDelegate requestHandler)
            : base(Stack)
        {
            this.RequestHandler = requestHandler;
        }

        public override void Dispose()
        {
        }

        public override void OnRequest(IRequest request)
        {
            var response = request.CreateResponse(HttpStatusCode.OK, "KeePassHttpX");
            response.Body = new MemoryStream();

            RequestHandler(request, response);

            response.Body.Position = 0;

            Send(response);
        }
    }
}
