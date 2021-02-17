namespace PulsingServer.Test

open System
open System.Diagnostics
open PulsingServer
open NUnit.Framework
open FsUnitTyped

[<TestFixture>]
module TestPulsingServer =

    [<Test>]
    let ``Example test scenario`` () =
        let responder1 = ServerAgent.make "hi"
        let responder2 = ServerAgent.make "hi"

        let mutable info = "original info"
        let count = ref 0

        let getInfo =
            async {
                System.Threading.Interlocked.Increment count
                |> ignore

                let info = lock info (fun () -> sprintf "%s" info)
                return info
            }

        let dontSleep (_ : TimeSpan) = async { return () }

        let infoProvider =
            ExternalInfoProvider.make dontSleep getInfo 10<ms> [| responder1 ; responder2 |]

        // We're not getting new info, because we didn't await the construction of ExternalInfoProvider
        count.Value |> shouldEqual 0

        // The two responders are ready, but have not received anything yet.
        do
            let response = ServerAgent.giveNextResponse responder1

            response
            |> Async.RunSynchronously
            |> shouldEqual "hi"

        // Now start off the ExternalInfoProvider!
        let _ = infoProvider |> Async.RunSynchronously

        // Now we have definitely started pinging...
        count.Value |> shouldBeGreaterThan 0

        // The two responders are ready, but have not received anything yet.
        do
            let response = ServerAgent.giveNextResponse responder1

            response
            |> Async.RunSynchronously
            |> shouldEqual "original info"

        // Update the info. responder1 is not going to fail on the `received` check, because that
        // was one-shot.
        lock info (fun () -> info <- "new info!")

        // Get responder2 ready to act in a couple of different ways.
        let response2 = ServerAgent.giveNextResponse responder2
        let response2' = ServerAgent.giveNextResponse responder2

        // At some point soon, the infoProvider picks up the change and propagates it.

        response2
        |> Async.RunSynchronously
        |> fun info ->
            // By design, we can't distinguish between these two cases.
            (info = "new info!" || info = "original info")
            |> shouldEqual true

        response2'
        |> Async.RunSynchronously
        |> fun info ->
            // By design, we can't distinguish between these two cases.
            (info = "new info!" || info = "original info")
            |> shouldEqual true

        // Eventually, responder2 does pick up the new info.
        let rec go () =
            let response =
                ServerAgent.giveNextResponse responder2
                |> Async.RunSynchronously

            if response <> "new info!" then
                go ()

        go ()

    [<TestCase(10000, 1)>]
    [<TestCase(10000, 3)>]
    let ``Stress test`` (n : int, queues : int) =
        let responders =
            Array.init queues (fun _ -> ServerAgent.make "uninitialised")

        let mutable data = ""

        let getInfo =
            async {
                // Simulate a slow network call
                do! Async.Sleep (TimeSpan.FromSeconds 1.)
                let result = lock data (fun () -> sprintf "%s" data)
                return result
            }

        let _infoProvider =
            ExternalInfoProvider.make Async.Sleep getInfo 10<ms> responders
            |> Async.RunSynchronously

        let time = Stopwatch ()
        // Restart it a couple of times to warm it up
        time.Restart ()
        time.Restart ()

        // n requests come in - note that we don't start them off yet,
        // because we want to time them separately
        let requests =
            Array.init
                n
                (fun i ->
                    async {
                        let! answer = ServerAgent.giveNextResponse responders.[i % queues]

                        if answer <> "" then
                            failwith "unexpected response!"

                        return ()
                    })
            |> Async.Parallel
            |> Async.Ignore

        time.Stop ()
        printfn "Time to construct requests: %i ms" time.ElapsedMilliseconds

        time.Restart ()

        requests |> Async.RunSynchronously

        time.Stop ()
        printfn "Time to execute: %i ms" time.ElapsedMilliseconds

        // Now prepare n more requests, but halfway through, we'll be changing the data.
        // Again, don't kick them off right now; wait for the timer.
        time.Restart ()

        let requests =
            Array.init
                n
                (fun i ->
                    if i = n / 2 then
                        async {
                            lock data (fun () -> data <- "new data")
                            return None
                        }
                    else
                        async {
                            do! Async.Sleep (TimeSpan.FromMilliseconds (float i))
                            let! response = ServerAgent.giveNextResponse (responders.[i % queues])
                            return Some response
                        })
            |> Async.Parallel

        time.Stop ()

        printfn "Time to construct requests: %i ms" time.ElapsedMilliseconds

        time.Restart ()

        let results = requests |> Async.RunSynchronously

        time.Stop ()
        printfn "Time to execute: %i ms" time.ElapsedMilliseconds

        let grouped =
            results |> Array.countBy id |> Map.ofArray

        grouped.[None] |> shouldEqual 1

        let pre =
            Map.tryFind (Some "") grouped
            |> Option.defaultValue 0

        let post =
            Map.tryFind (Some "new data") grouped
            |> Option.defaultValue 0

        pre + post |> shouldEqual (n - 1)
        printfn "Got old data: %i. Got new data: %i." pre post
