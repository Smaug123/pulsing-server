open System.Net
open System.Diagnostics

let timer = new Stopwatch()
timer.Restart()

[ for i in 1 .. 1000 do
    yield
        async {
            let w = new WebClient()
            return w.DownloadString("http://localhost:5000/")
        } ]
|> Async.Parallel
|> Async.RunSynchronously

timer.Stop()
printfn "%+A" timer.ElapsedMilliseconds
