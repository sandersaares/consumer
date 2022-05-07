# consumer

An app that simply consumes resources

Exe usage: `Consumer.exe --cpu-cores 2 --memory-gb 8`

Docker usage: `docker run --rm -it sandersaares/consumer --cpu-cores 2 --memory-gb 8`

The app publishes Prometheus metrics about resource usage, by default on HTTP port 5000 (`--metrics-port`).