namespace SampleApplication

open System
open System.IO
open System.Net
open System.Net.Http
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting

open PulsingServer

type private State =
    /// We have parsed the first digit of the hour, and it is this.
    | ParsedHourFirstDigit of byte
    /// We have parsed the entire hour, and it is this. We await a colon.
    | ParsedHourAwaitingColon of byte
    /// We have parsed the entire hour, and it is this. We await the first digit of the minute.
    | ParsedHour of byte
    /// We have parsed the entire hour, and the first digit of the minute.
    | ParsedMinuteFirstDigit of byte * byte
    /// We have parsed the entire hour, and the entire minute. We are now waiting for a colon.
    | ParsedMinuteAwaitingColon of byte * byte
    /// We have parsed the entire hour, and the entire minute. We are now waiting for the first digit of the second.
    | ParsedMinute of byte * byte
    /// We have parsed the hour and minute completely, and have parsed the first digit of the second.
    | ParsedSecondFirstDigit of byte * byte * byte
    | Waiting

type private Date =
    {
        Hour : byte
        Minute : byte
        Second : byte
    }

type private ParseOutput =
    | StreamEnded
    | State of State
    | Complete of Date

type Startup() =

    let client = new WebClient ()

    let servers =
        Array.init
            2
            (fun _ ->
                ServerAgent.make (
                    Some
                        {
                            Hour = 0uy
                            Minute = 0uy
                            Second = 0uy
                        }
                ))

    /// Convert an input byte to the integer digit it is.
    /// For example, ord('0') will match to Char(0).
    let (|Char|_|) (b : byte) : byte option =
        if byte '0' <= b && b <= byte '9' then Some (b - byte '0') else None

    /// Extremely rough-and-ready function to get a time out of a stream which contains
    /// text.
    /// We expect the time to be expressed as hh:mm:ss
    /// and we do not bother our pretty little heads with Unicode issues.
    let parseDateInner (buffer : byte array) (s : Stream) (state : State) : Async<ParseOutput> =
        async {
            let! written = s.ReadAsync (buffer, 0, 1) |> Async.AwaitTask

            if written = 0 then
                return StreamEnded
            else

                match state with
                | Waiting ->
                    match buffer.[0] with
                    | Char b -> return State (State.ParsedHourFirstDigit b)
                    | _ -> return State Waiting
                | ParsedHourFirstDigit hour ->
                    match buffer.[0] with
                    | Char b -> return State (State.ParsedHourAwaitingColon (hour * 10uy + b))
                    | _ -> return State Waiting
                | ParsedHourAwaitingColon hour ->
                    match buffer.[0] with
                    | b when b = byte ':' -> return State (State.ParsedHour hour)
                    | _ -> return State Waiting
                | ParsedHour hour ->
                    match buffer.[0] with
                    | Char b -> return State (State.ParsedMinuteFirstDigit (hour, b))
                    | _ -> return State Waiting
                | ParsedMinuteFirstDigit (hour, min) ->
                    match buffer.[0] with
                    | Char b -> return State (State.ParsedMinuteAwaitingColon (hour, 10uy * b + min))
                    | _ -> return State Waiting
                | ParsedMinuteAwaitingColon (hour, min) ->
                    match buffer.[0] with
                    | b when b = byte ':' -> return State (State.ParsedMinute (hour, min))
                    | _ -> return State Waiting
                | ParsedMinute (hour, min) ->
                    match buffer.[0] with
                    | Char b -> return State (State.ParsedSecondFirstDigit (hour, min, b))
                    | _ -> return State Waiting
                | ParsedSecondFirstDigit (hour, min, sec) ->
                    match buffer.[0] with
                    | Char b ->
                        return
                            Complete
                                {
                                    Hour = hour
                                    Minute = min
                                    Second = 10uy * sec + b
                                }
                    | _ -> return State Waiting
        }

    let parseDate (stream : Stream) : Async<Date option> =
        async {
            let buffer = [| 0uy |]

            let rec go (state : State) =
                async {
                    match! parseDateInner buffer stream state with
                    | StreamEnded -> return None
                    | Complete d -> return Some d
                    | State state -> return! go state
                }

            return! go State.Waiting
        }

    let update : Async<Date option> =
        async {
            // Note: there is absolutely no error handling here at all.
            // Obviously that would be desirable.
            let result =
                client.OpenRead (Uri "https://www.timeanddate.com/worldclock/uk")

            return! parseDate result
        }

    // Note that we haven't kicked this off yet - it's still an Async
    let pulses =
        ExternalInfoProvider.make Async.Sleep update 500<ms> servers

    // This method gets called by the runtime. Use this method to add services to the container.
    // For more information on how to configure your application, visit https://go.microsoft.com/fwlink/?LinkID=398940
    member _.ConfigureServices (services : IServiceCollection) =
        pulses |> Async.RunSynchronously |> ignore

    // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
    member _.Configure (app : IApplicationBuilder, env : IWebHostEnvironment) =
        if env.IsDevelopment () then
            app.UseDeveloperExceptionPage () |> ignore

        app
            .UseRouting()
            .UseEndpoints(fun endpoints ->
                endpoints.MapGet (
                    "/",
                    fun context ->
                        async {
                            let! answer = ServerAgent.giveNextResponse servers.[0]

                            match answer with
                            | None ->
                                do!
                                    context.Response.WriteAsync ("Oh noes")
                                    |> Async.AwaitTask
                            | Some date ->
                                do!
                                    context.Response.WriteAsync (sprintf "%i:%i:%i" date.Hour date.Minute date.Second)
                                    |> Async.AwaitTask

                            return ()
                        }
                        |> Async.StartAsTask
                        :> Task
                )
                |> ignore)
        |> ignore
