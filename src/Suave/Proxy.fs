/// A module for proxying requests to another computer/ip/port
module Suave.Proxy

open System
open System.IO
open System.Net
open System.Collections.Generic

open Suave.Utils
open Suave.Utils.Bytes

open Suave.Sockets
open Suave.Sockets.Control
open Suave.Tcp

open Suave.Response
open Suave.ParsingAndControl

/// Copies the headers from 'headers1' to 'headers2'
let private toHeaderList (headers : WebHeaderCollection) =
  headers.AllKeys
  |> Seq.map (fun key -> key, headers.[key])
  |> List.ofSeq

/// Send the web response from HttpWebResponse to the HttpRequest 'p'
let private sendWebResponse (data : HttpWebResponse) ({ request = { trace = t }; response = resp } as ctx : HttpContext) = async {
  let headers = toHeaderList data.Headers 
  //"-> readFully" |> Log.verbose ctx.runtime.logger "Suave.Proxy.sendWebResponse:GetResponseStream" ctx.request.trace
  use ms = new MemoryStream()
  do! data.GetResponseStream().CopyToAsync ms
  let bytes = ms.ToArray()
  //"<- readFully" |> Log.verbose ctx.runtime.logger "Suave.Proxy.sendWebResponse:GetResponseStream" ctx.request.trace

  let ctxNext =
    { ctx with response = { resp with headers = resp.headers @ headers } }

  let code =
    match data.StatusCode |> int |> HttpCode.tryParse with
    | Choice1Of2 x -> x
    | Choice2Of2 err -> failwith err

  return! response code bytes ctxNext
  }

let transferStreamBounded (toStream : Connection) (from : Stream) len =
  let bufSize = 0x2000
  let buf = Array.zeroCreate<byte> bufSize
  let rec doBlock left = socket {
    let! read = SocketOp.ofAsync <| from.AsyncRead(buf, 0, Math.Min(bufSize, left))
    if read <= 0 || left - read = 0 then
      return ()
    else
      do! Connection.send toStream (new ArraySegment<_>(buf,0,read))
      return! doBlock (left - read) }
  doBlock len

/// response_f writes the HTTP headers regardles of the setting of context.writePreamble
/// it is currently only used in Proxy.fs
let response_f (context: HttpContext) =
  
  //Suave.HttpOutput.writePreamble [] context
  Suave.HttpOutput.writeContent true context context.response.content


/// Forward the HttpRequest 'p' to the 'ip':'port'
let forward (ip : IPAddress) (port : uint16) : HttpContext -> Async<HttpContext option> =
  fun ctx -> async {
    let p = ctx.request
    let buildWebHeadersCollection (h : NameValueList) =
      let r = new WebHeaderCollection()
      for e in h do
        let key = fst e
        if not (WebHeaderCollection.IsRestricted key) then
          r.Add(key, snd e)
      r
    let mutable m= "http"
    if p.rawQuery.Contains("session") then
      printfn "obacht"
      m <- "ws"
    let url = new UriBuilder(m, ip.ToString(), int port, p.url.AbsolutePath, "?" + p.rawQuery)
    let q = WebRequest.Create(url.Uri) :?> HttpWebRequest

    q.AllowAutoRedirect         <- false
    q.AllowReadStreamBuffering  <- false
    q.AllowWriteStreamBuffering <- false
    q.Method                    <- string p.``method``
    q.Headers                   <- buildWebHeadersCollection p.headers
    q.Proxy                     <- null

    //copy restricted headers
    let header s = getFirst p.headers s
    header "accept"            |> Choice.iter (fun v -> q.Accept <- v)
    header "date"              |> Choice.iter (fun v -> q.Date <- DateTime.Parse v)
    header "expect"            |> Choice.iter (fun v -> q.Expect <- v)
    header "host"              |> Choice.iter (fun v -> q.Host <- v)
    header "range"             |> Choice.iter (fun v -> q.AddRange(Int64.Parse v))
    header "referer"           |> Choice.iter (fun v -> q.Referer <- v)
    header "content-type"      |> Choice.iter (fun v -> q.ContentType <- v)
    header "content-length"    |> Choice.iter (fun v -> q.ContentLength <- Int64.Parse(v))
    header "if-modified-since" |> Choice.iter (fun v -> q.IfModifiedSince <- DateTime.Parse v)
    header "transfer-encoding" |> Choice.iter (fun v -> q.TransferEncoding <- v)
    header "user-agent"        |> Choice.iter (fun v -> q.UserAgent <- v)

    q.Headers.Add("X-Real-IP", ctx.clientIpTrustProxy.ToString())

    if p.``method`` = HttpMethod.POST || p.``method`` = HttpMethod.PUT then
      match p.headers %% "content-length" with
      | Choice1Of2 contentLength ->
        let! _ = transferStreamBounded ctx.connection (q.GetRequestStream()) (int32 contentLength)
        ()
      | _ -> ()

    try
      let! data = q.AsyncGetResponse()
      let! res = sendWebResponse ((data : WebResponse) :?> HttpWebResponse) ctx
      match res with
      | Some newCtx ->
        //let! _ =  response_f newCtx
        return Some newCtx
      | None -> return None
    with
    | :? WebException as ex when ex.Response <> null ->
      let! res = sendWebResponse (ex.Response :?> HttpWebResponse) ctx
      match res with
      | Some newCtx ->
        //let! _ = SocketOp.ofAsync <| response_f newCtx
        return Some newCtx
      | _ -> return None
    | :? WebException as ex when ex.Response = null ->
      let! res = response HTTP_502 (UTF8.bytes "suave proxy: Could not connect to upstream") ctx
      match res with
      | Some newCtx ->
        let! _ =  response_f newCtx
        return Some newCtx
      | _ -> return None
  } 

let private proxy proxyResolver (r : HttpContext) =
  match proxyResolver r.request with
  | Some (ip, port) -> forward ip port r
  | None            -> failwith "invalid request."

/// Run a proxy server with the given configuration and given upstream/target
/// resolver.
let createReverseProxyServerAsync (config : SuaveConfig) resolver =
  let homeFolder, compressionFolder =
    ParsingAndControl.resolveDirectory config.homeFolder,
    Path.Combine(ParsingAndControl.resolveDirectory config.compressedFilesFolder, "_temporary_compressed_files")

  let all =
    [ for binding in config.bindings do 
        let runtime = SuaveConfig.toRuntime config homeFolder compressionFolder binding
        let p = (proxy resolver)

        let reqLoop = ParsingAndControl.requestLoop runtime p
        let tcpServer = config.tcpServerFactory.create(config.maxOps, config.bufferSize, config.autoGrow, binding.socketBinding)
        let server = startTcpIpServerAsync reqLoop binding.socketBinding tcpServer
        yield server ]
      
  let listening = all |> List.map fst |> Async.Parallel |> Async.Ignore
  let server    = all |> List.map snd |> Async.Parallel |> Async.Ignore
  listening, server

let startReverseProxyServer config resolver =
  Async.RunSynchronously(createReverseProxyServerAsync config resolver |> snd,
    cancellationToken = config.cancellationToken)