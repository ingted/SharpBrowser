open SharpBrowser
open System
open System.Windows.Forms

open FAkka.Shared.DictionarySerialization

open System
open System.IO
open System.Collections.Immutable
open Akka.Actor
open Akka.Cluster.Tools.PublishSubscribe
open Akka.Persistence
open Akkling
open FAkka.Shared
open FAkka.Shared.FActorSystem
open FAkka.Shared.FActorSystemConfig

open System.Collections.Generic
open System.Data
open FAkka.Util.Common
open FAkka.Util.Configuration
open FAkka.Logging.Type
open FSharp.Collections.ParallelSeq

open FAkka.Shared.Data
open FAkka.Util
open FAkka.Shared.FAkkaConfiguration
open FAkka.Shared.Global.Types
open FAkka.Shared.Global.Schedulers
open FAkka.Shared.Global.Spawn
open FAkklingSpawn
open Chiron
open Chiron.Json
open System.Threading

open FAkka.CQRS
open TrivialQueryExecutionServices

open System.Collections.Concurrent
open FAkka.Util.Common.FAkkaCompression
open FAkka.Util.Networking
open FAkka.Util.FsiContext

open System.Reflection


open System.Threading.Tasks
open CefSharp.WinForms
open CefSharp

Application.SetCompatibleTextRenderingDefault(false)

setFAkkaConfig "./FApp PubSub.json"
let fakkaConf = getFAkkaConfig "FApp PubSub.json".some
let miscConf = fakkaConf["misc"]
let signalNodeRole = jstr miscConf["signalNodeRole"]


//let sqlConn161 = SQLServer (Choice2Of2 "AkkaMDC_sa_to161") //20230917 架構改 161 93 分流，取消用這個
let sqlConn94_DEV = SQLServer (Choice2Of2 "AkkaMDC_DEV_199.143to94_odswriter")
let sqlConn98     = SQLServer (Choice2Of2     "AkkaMDC_199.143to98_sa")
//FActorSystemConfig.registry.RegisterFactory taskPickler<ImmutableHashSet<string> option>

//let m = "hisResp.dept.CME-9051-4".``match`` "[^\.]+\.[^\.]+\.(?<ex>[^\-]+)-"
//m.Groups["ex"].Value

//let a = "NQ.2309-4".``match`` "^(?<comm>[^\-]+)-"
//a.Groups["comm"].Value



let p = getFreePort () 
let pbmp = getFreePort () 

let conf0 = {
        getDefault ()
            with 
                port = p
                pubPort = p
                PBPort = pbmp } |> Some


let experimentMode = "dev143"
let candleMode = "dev"
let conf =
    match experimentMode with
    | "dev143" ->
        Some {
            conf0.Value
                with
                    seedHost = getMyIp ()
            }
    | _ ->
        conf0


let fasys = getDefaultActorSystemLocalWithTagger conf ["ShardAnalyticServiceNode"; "ShardNode"; ] 1 true None None []
let extSys = fasys.asys :?> ExtendedActorSystem
let serializer = ExprSerializer extSys



//fasys.asys.Settings.Config


let addr, sqlConn = 
    let akkaNode = conf.Value 
        
    let a = Address.Parse (sprintf "akka.tcp://%s@%s:%d" fasys.asys.Name akkaNode.seedHost akkaNode.seedPort)
    match candleMode with
    | "dev" ->
        a, sqlConn94_DEV
    | "prod" ->
        a, sqlConn98
//let addr = fasys.cluster.SelfAddress

//conn sqlConn

//fasys.asys.Terminate()
fasys.cluster.JoinSeedNodes [|addr|]
//fasys.cluster.State
//fasys.asys.Settings

let cts = new CancellationTokenSource()
let ct = cts.Token

//let connObj = conn sqlConn
//let fqSupervisor =
//    ODSWriter.genFQSupervisor [
//            SinglePrepare (
//                sqlConn //tick data source
//                , ODSWriter.odsWriterQEHandler "FAkkaAnalytica_qh_22301"
//                , ODSWriter.odsWriterQEErrHandler "FAkkaAnalytica_qeh_22301"
//                , 22301)
//        ] ct "FAkkaAnalytica"

//let fqSupervisorWrapper = fqSupervisor.spawn fasys.asys "fqSupervisor"

//let fqActor = fqSupervisorWrapper.watchee


let loc = FAkka.Util.Common.getCurrentAssemblyLoc ()
let fi = new System.IO.FileInfo(loc)
let fp = (fi.DirectoryName.Replace(@"\", "/"))


Application.EnableVisualStyles()
let mf = new MainForm()

let refs = 
    AppDomain.CurrentDomain.GetAssemblies() 
    |> Array.choose (fun a -> 
        let l = a.Location.Replace(@"\", "/")
        if l.Trim() = "" then None
        else
            $"-r:{l}".some
    )
    |> Array.sort
    |> Array.append [|
        //"--utf8output"; 
        $"--lib:\"{fp}\""
    |]


let fsiActor, ctsActor, cd = genSimpleFsiActor "fsi" fasys.asys refs

let rpath = fsiActor.remotePathStr fasys.asys


let _ = MessageBox.Show(rpath)

let _ = cd.TryAdd("cbl", mf.cbl)

type Helper (browser:ChromiumWebBrowser, mre:ManualResetEvent, htmlHandlerOpt:(string -> unit) option) =

    let mutable htmlHandler =
        match htmlHandlerOpt with
        | None -> fun _ -> ()
        | Some f -> f
    
    member this.GetSourceBase () =
        let exec = browser.GetSourceAsync()
        ignore <| exec.ContinueWith(Action<Task<string>>(fun s -> 
                                                                htmlHandler s.Result                                                                
                                                                ignore <| mre.Set ()
        ))

    member this.GetSource handler (returnFun: unit -> 'T)  =
        htmlHandler <- handler
        this.GetSourceBase ()
        let _ = mre.WaitOne ()
        let _ = mre.Reset()
        returnFun ()

    member this.BrowserLoadingStateChanged (sender:obj) (e:FrameLoadEndEventArgs) =
        if (e.Frame.IsMain || browser.Address = "")
        then
            browser.FrameLoadEnd.RemoveHandler <| new EventHandler<_>(this.BrowserLoadingStateChanged)
            //ignore <| mre.WaitOne ()
            this.GetSourceBase ()

    member this.execHtmlHandler<'T> url handler (returnFun: unit -> 'T) =
        htmlHandler <- handler
        let disposable = 
            browser.FrameLoadEnd.Subscribe (
                fun x -> 
                    this.BrowserLoadingStateChanged browser x
                    
            )
        let r = browser.EvaluateScriptAsync("").Result.
        browser.Load url
        let _ = mre.WaitOne ()
        disposable.Dispose ()
        let _ = mre.Reset()
        returnFun ()

let newHelper (b, hOpt) = 
    let m = new ManualResetEvent(false)
    new Helper (b, m, hOpt)

let _ = cd.TryAdd("newHelper", Func<ChromiumWebBrowser*(string -> unit)option,Helper> newHelper)

Application.Run(mf)


let a = 123


//let genHelper = (unbox<Func<ChromiumWebBrowser * (string -> unit) option, Helper>> cd.["newHelper"])
//let b = (unbox<List<ChromiumWebBrowser>> cd.["cbl"]).[0]
//let v = ref ""
//let f = fun s -> v.Value <- s
//let h = genHelper.Invoke (b, Some f)
//h.execHtmlHandler<string> "www.google.com" f (fun () -> v.Value)



