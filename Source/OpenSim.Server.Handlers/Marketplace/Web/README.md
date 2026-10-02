# Marketplace web interface

This directory contains the initial static Marketplace storefront.

It is intentionally independent from the Robust process: the files can be served by any normal HTTP server, reverse proxy, or static hosting service. The Marketplace backend/API remains the authoritative OpenSimulator service.

## Initial taxonomy

The category IDs and names are based on the Marketplace taxonomy supplied for the Tranquillity Marketplace. The IDs are preserved in the query string so the future listing API can use the same category identifiers.

## Current scope

- Marketplace header and search control
- Category navigation
- Featured-listings area
- Responsive layout
- Empty-state suitable for a new Marketplace

The listing grid is intentionally not populated with fake products. It will be connected to the real Marketplace service once the web listing/query API is implemented.

## Intended deployment

A typical deployment can serve this directory at the public Marketplace hostname and proxy API requests to Robust:

    Browser / Firestorm web view
        |
        +--> static Marketplace frontend
        |
        +--> Robust Marketplace API
                  |
                  +--> Marketplace service
                  +--> Marketplace database
                  +--> Money / Inventory services
