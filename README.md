This is my answer to https://github.com/JerryDot/Webscraping-Webserver .

It comes in two parts: a small library which supplies the moving parts, and then a whale of an ASP.NET server to demonstrate it.

# PulsingServer library

This is a small library that defines two entities based on `MailboxProcessor`s.
One entity (`ServerAgent`) sits ready to serve requests based on the latest information pushed into it.
One entity (`ExternalInfoProvider`) repeatedly calls an `Async` to obtain new information, and pushes it into the `ServerAgent`s.

This way, we can ensure that only one entity is obtaining new information for the system, with low latency for responses via the `ServerAgent`, and no concurrency issues (because each `ServerAgent` maintains its own copy of the latest state).

# ASP.NET server

The server demonstrates the library in the most naive possible way, scraping a web page every 500ms to find the current time.

This incidentally demonstrates that you can distribute load across multiple `ServerAgent`s, although in fact the example does not do any distributing.
