# Local NATS TLS testing

These certificates are strictly for local development. The generated CA private
key and all endpoint private keys must remain uncommitted.

## Generate the certificates

From a Command Prompt opened at the repository root:

```bat
scripts\generate-nats-test-certificates.bat
```

The default output is the gitignored `test-certificates` directory. An unused
alternative directory can be passed as the first argument:

```bat
scripts\generate-nats-test-certificates.bat C:\temp\nats-test-certs
```

The script requires `openssl.exe` on `PATH` and deliberately refuses to
overwrite an existing CA.

## Which file is used where?

| File | Use | Keep secret? |
| --- | --- | --- |
| `ca.pem` | CA trust certificate used by the NATS server, clients, and the manager's HTTPS monitor | No |
| `ca.key` | CA signing key; only needed to issue more test certificates | **Yes** |
| `nats-server.pem` | Certificate presented by NATS on `nats://localhost:4223` and `https://localhost:8223` | No |
| `nats-server.key` | Private key matching `nats-server.pem` | **Yes** |
| `nats-client.pem` | Certificate presented by a mutually authenticated NATS client | No |
| `nats-client.key` | Private key matching `nats-client.pem` | **Yes** |

The server certificate is valid for `localhost`, `127.0.0.1`, and `::1` and has
the TLS server-authentication EKU. The client certificate has only the TLS
client-authentication EKU.

## Messaging Server Manager fields

Configure the **Local NATS TLS** definition as follows:

- Client port: `4223`
- Monitoring port: `8223`
- Use TLS and HTTPS monitoring: enabled
- Server certificate file: `test-certificates\nats-server.pem`
- Server private-key file: `test-certificates\nats-server.key`
- CA certificate file: `test-certificates\ca.pem`
- Monitoring client certificate file: `test-certificates\nats-client.pem`
- Monitoring client private-key file: `test-certificates\nats-client.key`
- Verify client certificates: enabled to test mutual TLS; disabled to test
  server-only TLS

The manager starts NATS with TLS on port 4223 and HTTPS monitoring on port 8223.
The optional monitoring client certificate and key allow `/varz` collection
when the HTTPS listener or an intervening corporate gateway requires mutual TLS.
Use the same monitoring credentials on a remote definition that points at this
server to exercise the remote UI path.

## Equivalent NATS server command

```bat
nats-server.exe --port 4223 --https_port 8223 --tls --tlsverify ^
  --tlscert test-certificates\nats-server.pem ^
  --tlskey test-certificates\nats-server.key ^
  --tlscacert test-certificates\ca.pem
```

## Connect using the NATS CLI

For mutual TLS, the client trusts the CA and presents its own certificate and
private key:

```bat
nats --server tls://localhost:4223 ^
  --tlsca test-certificates\ca.pem ^
  --tlscert test-certificates\nats-client.pem ^
  --tlskey test-certificates\nats-client.key server check connection
```

If **Verify client certificates** is disabled, omit `--tlscert` and `--tlskey`,
but retain `--tlsca` so the client verifies the NATS server.

## Test HTTPS monitoring

Open `https://localhost:8223/varz` or use:

```bat
curl.exe --cacert test-certificates\ca.pem ^
  --cert test-certificates\nats-client.pem ^
  --key test-certificates\nats-client.key ^
  https://localhost:8223/varz
```

If monitoring does not require a client certificate, omit `--cert` and `--key`.
Endpoint-security products that perform HTTPS inspection may replace the
localhost certificate. Exclude localhost/port 8223 from TLS inspection before
running the complete local-and-remote integration assertion.

Browsers will warn unless `ca.pem` is imported into the Windows trust store.
Importing it is unnecessary for the manager, NATS CLI, and `curl` examples and
is not recommended for an ephemeral test CA.
