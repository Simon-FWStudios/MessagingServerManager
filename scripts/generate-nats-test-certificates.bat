@echo off
setlocal EnableExtensions

where openssl.exe >nul 2>&1
if errorlevel 1 (
  echo ERROR: openssl.exe was not found on PATH.
  echo Install OpenSSL for Windows or add its bin directory to PATH, then retry.
  exit /b 1
)

set "OUTPUT_DIR=%~1"
if not defined OUTPUT_DIR set "OUTPUT_DIR=%~dp0..\test-certificates"
for %%I in ("%OUTPUT_DIR%") do set "OUTPUT_DIR=%%~fI"

if exist "%OUTPUT_DIR%\ca.key" (
  echo ERROR: Certificates already exist in:
  echo   %OUTPUT_DIR%
  echo Use a new output directory or remove the old test certificates explicitly.
  exit /b 2
)

mkdir "%OUTPUT_DIR%" 2>nul
if errorlevel 1 (
  echo ERROR: Could not create output directory: %OUTPUT_DIR%
  exit /b 3
)

set "SERVER_EXT=%OUTPUT_DIR%\server-extensions.cnf"
set "CLIENT_EXT=%OUTPUT_DIR%\client-extensions.cnf"
set "OPENSSL_TEST_CONF=%OUTPUT_DIR%\openssl-test.cnf"
set "OPENSSL_CONF=%OPENSSL_TEST_CONF%"

>"%OPENSSL_TEST_CONF%" echo [req]
>>"%OPENSSL_TEST_CONF%" echo distinguished_name=req_distinguished_name
>>"%OPENSSL_TEST_CONF%" echo [req_distinguished_name]

>"%SERVER_EXT%" echo [server_cert]
>>"%SERVER_EXT%" echo basicConstraints=critical,CA:FALSE
>>"%SERVER_EXT%" echo keyUsage=critical,digitalSignature,keyEncipherment
>>"%SERVER_EXT%" echo extendedKeyUsage=serverAuth
>>"%SERVER_EXT%" echo subjectKeyIdentifier=hash
>>"%SERVER_EXT%" echo authorityKeyIdentifier=keyid,issuer
>>"%SERVER_EXT%" echo subjectAltName=@server_names
>>"%SERVER_EXT%" echo [server_names]
>>"%SERVER_EXT%" echo DNS.1=localhost
>>"%SERVER_EXT%" echo IP.1=127.0.0.1
>>"%SERVER_EXT%" echo IP.2=::1

>"%CLIENT_EXT%" echo [client_cert]
>>"%CLIENT_EXT%" echo basicConstraints=critical,CA:FALSE
>>"%CLIENT_EXT%" echo keyUsage=critical,digitalSignature
>>"%CLIENT_EXT%" echo extendedKeyUsage=clientAuth
>>"%CLIENT_EXT%" echo subjectKeyIdentifier=hash
>>"%CLIENT_EXT%" echo authorityKeyIdentifier=keyid,issuer

echo Generating test certificate authority...
openssl genpkey -quiet -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out "%OUTPUT_DIR%\ca.key"
if errorlevel 1 goto :failed
openssl req -x509 -new -sha256 -days 3650 -key "%OUTPUT_DIR%\ca.key" -out "%OUTPUT_DIR%\ca.pem" -subj "/CN=Messaging Server Manager Test CA" -addext "basicConstraints=critical,CA:TRUE" -addext "keyUsage=critical,keyCertSign,cRLSign" -addext "subjectKeyIdentifier=hash"
if errorlevel 1 goto :failed

echo Generating localhost NATS server certificate...
openssl genpkey -quiet -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out "%OUTPUT_DIR%\nats-server.key"
if errorlevel 1 goto :failed
openssl req -new -sha256 -key "%OUTPUT_DIR%\nats-server.key" -out "%OUTPUT_DIR%\nats-server.csr" -subj "/CN=localhost"
if errorlevel 1 goto :failed
openssl x509 -req -sha256 -days 825 -in "%OUTPUT_DIR%\nats-server.csr" -CA "%OUTPUT_DIR%\ca.pem" -CAkey "%OUTPUT_DIR%\ca.key" -CAcreateserial -out "%OUTPUT_DIR%\nats-server.pem" -extfile "%SERVER_EXT%" -extensions server_cert
if errorlevel 1 goto :failed

echo Generating NATS client certificate...
openssl genpkey -quiet -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out "%OUTPUT_DIR%\nats-client.key"
if errorlevel 1 goto :failed
openssl req -new -sha256 -key "%OUTPUT_DIR%\nats-client.key" -out "%OUTPUT_DIR%\nats-client.csr" -subj "/CN=nats-test-client"
if errorlevel 1 goto :failed
openssl x509 -req -sha256 -days 825 -in "%OUTPUT_DIR%\nats-client.csr" -CA "%OUTPUT_DIR%\ca.pem" -CAkey "%OUTPUT_DIR%\ca.key" -CAserial "%OUTPUT_DIR%\ca.srl" -out "%OUTPUT_DIR%\nats-client.pem" -extfile "%CLIENT_EXT%" -extensions client_cert
if errorlevel 1 goto :failed

openssl verify -CAfile "%OUTPUT_DIR%\ca.pem" -purpose sslserver "%OUTPUT_DIR%\nats-server.pem"
if errorlevel 1 goto :failed
openssl verify -CAfile "%OUTPUT_DIR%\ca.pem" -purpose sslclient "%OUTPUT_DIR%\nats-client.pem"
if errorlevel 1 goto :failed

del /q "%OUTPUT_DIR%\nats-server.csr" "%OUTPUT_DIR%\nats-client.csr" "%SERVER_EXT%" "%CLIENT_EXT%" "%OPENSSL_TEST_CONF%" "%OUTPUT_DIR%\ca.srl" 2>nul

echo.
echo Certificates created successfully in:
echo   %OUTPUT_DIR%
echo.
echo Server: nats-server.pem + nats-server.key
echo Client: nats-client.pem + nats-client.key
echo Trust:  ca.pem ^(used by both server and client^)
echo.
echo See docs\NATS-TLS-TESTING.md for configuration and test commands.
exit /b 0

:failed
echo.
echo ERROR: OpenSSL certificate generation failed.
echo Partial output remains in: %OUTPUT_DIR%
exit /b 4
