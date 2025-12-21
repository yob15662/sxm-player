# sxm-player

## Description

This project provides a SiriusXM streaming proxy for playback of SiriusXM channels on headless Linux systems. It acts as an Icecast-compatible server, allowing you to use standard music players like MPD (Music Player Daemon), VLC, or any other client that supports Icecast streams to listen to SiriusXM content.

The proxy authenticates with SiriusXM's API, retrieves streaming URLs for channels, and re-streams the content in a format that's compatible with popular media players. This enables seamless integration of SiriusXM into your existing audio setup, automation workflows, or home media systems. 

Just like the web player, you will need an active Sirius XM account to use the proxy. 

## Development

The `sxm_cleaned.yaml` Open API was generated from the public web player.

### Avantation

Avantation is a tool that converts HAR files into OpenAPI specifications. Install it globally using npm:

```bash
npm install -g avantation
```

This tool will analyze the captured API traffic and generate a structured API specification.
 
### Convert Web Capture (HAR) to OpenAPI

Run Avantation on the captured HAR file to generate an OpenAPI specification:

```bash
avantation can.siriusxm.com.har --host=api.edge-gateway.siriusxm.com
```

The `--host` parameter ensures all API endpoints are mapped to the correct SiriusXM API gateway. This creates an OpenAPI YAML file that documents all the API endpoints, parameters, and response structures.

### Generate C# client code from OpenAPI

NSwag is used to generate strongly-typed C# client code from the OpenAPI specification:

```bash
nswag openapi2csclient sxm_cleaned.yaml
```

This command reads the `sxm_cleaned.yaml` OpenAPI specification and generates C# classes and methods that correspond to the SiriusXM API endpoints. The generated client code is used by the proxy to authenticate with SiriusXM and retrieve streaming URLs. The configuration for this code generation is defined in `nswag.json`.

NSwag is part of the solution build.
