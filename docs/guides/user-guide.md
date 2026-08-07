# User Guide

This guide covers how to use FSOps, feature by feature. FSOps is under active development, so most sections below describe intended behaviour rather than something you can click through today — each is clearly marked. Where a feature is live, it's described as it actually works.

## Table of contents

- [Current build](#current-build)
- [Creating your airline](#creating-your-airline)
- [Building routes](#building-routes)
- [Planning and flying a tracked flight](#planning-and-flying-a-tracked-flight)
- [Reading the post-flight report card](#reading-the-post-flight-report-card)
- [Hiring and assigning virtual pilots](#hiring-and-assigning-virtual-pilots)
- [Buying vs leasing aircraft](#buying-vs-leasing-aircraft)
- [Maintenance](#maintenance)
- [Statistics dashboards](#statistics-dashboards)

## Current build

Today, FSOps is an application shell: start the backend, open `http://localhost:5977` in your browser, and you'll see the UI load with a live connection to the server over SignalR. There's no airline to create yet and nothing to fly — this is the foundation the rest of the app is being built on. Everything below describes what's coming.

## Creating your airline

**Coming in a later update.**

What this is: the starting point for using FSOps. You'll give your airline a name, choose a home base airport, and pick a strategy that shapes how your airline behaves and where its opportunities lie:

- **International** — long-haul routes between major hubs.
- **Domestic** — shorter routes within a single country or region.
- **Low-cost** — high frequency, tight margins, cost-focused operations.
- **Premium** — fewer routes, higher fares, a focus on service quality.

How to do it: from the FSOps home screen, you'll start a "new airline" flow, fill in the airline name and home base, and select a strategy. This choice will influence route planning, ticket pricing, and demand modelling throughout the rest of the app.

## Building routes

**Coming in a later update.**

What this is: your route network is the set of city pairs your airline operates. Routes are where your strategy and home base actually turn into flying.

How to do it: from a routes screen, you'll search for destination airports, add a route from your home base (or another airport you already serve), and set basic parameters like the aircraft type you intend to fly it with. Routes you add become available to schedule and fly, and later, for virtual pilots to be assigned to.

## Planning and flying a tracked flight

**Coming in a later update.**

What this is: the core loop of FSOps. You pick a route, plan the flight, then fly it in MSFS while FSOps tracks it live via SimConnect.

How to do it: select a route and aircraft, then start the flight in FSOps before or after loading into the sim. Once MSFS reports you're airborne on a matching flight, FSOps begins tracking automatically: an interactive map follows your aircraft's position, and FSOps detects flight phases (taxi, take-off, climb, cruise, descent, approach, landing, taxi-in) as you fly. No manual phase reporting is needed — it's read directly from simulator state.

## Reading the post-flight report card

**Coming in a later update.**

What this is: once a tracked flight ends, FSOps produces a report card summarising how the flight went, both operationally and financially.

The report card will cover:

- **Landing quality** — scored on:
  - **Touchdown rate** (vertical speed at touchdown). As a rule of thumb, roughly **-100 to -200 feet per minute** is a smooth, well-judged touchdown. Beyond **-400 fpm**, the landing is considered hard and may flag for inspection or maintenance implications.
  - **G-force** at touchdown, as a secondary smoothness signal alongside touchdown rate.
  - **Centerline accuracy** — how closely you tracked the runway centreline through the flare and rollout.
- **Block time comparison** — how your actual gate-to-gate time compared with the planned block time for the route.

How to do it: no action needed to generate one — a report card is produced automatically at the end of every tracked flight and will be viewable from your flight history.

## Hiring and assigning virtual pilots

**Coming in a later update.**

What this is: as your airline grows, you won't want to fly every route yourself. Virtual pilots are hires who fly your scheduled routes automatically, on the real-world clock — including while FSOps itself is closed.

How to do it: from a pilots screen, you'll hire a pilot and assign them to one or more routes with a schedule. From that point, their flights complete in the background over real time and feed into your airline's economy and statistics the same way a flight you flew yourself would, just without the landing-quality detail that only comes from a flight tracked live through the sim.

## Buying vs leasing aircraft

**Coming in a later update.**

What this is: aircraft are the other half of running routes — you'll need them assigned to a route before it can be flown, by you or by a virtual pilot. FSOps is expected to support two ways to acquire one:

- **Buying** — a larger upfront cost, but the aircraft is yours outright with no ongoing payment.
- **Leasing** — lower upfront cost, spread as an ongoing expense against your airline's cash flow, but without ownership.

How to do it: from a fleet screen, you'll browse available aircraft, compare buy vs lease terms, and add the one you choose to your fleet before assigning it to a route.

## Maintenance

**Coming in a later update.**

What this is: aircraft accumulate wear through use — and harder landings are expected to accelerate it. Unmaintained aircraft are expected to become less reliable and more expensive to operate over time.

How to do it: from a fleet or maintenance screen, you'll be able to see each aircraft's condition and schedule maintenance, which costs money and takes the aircraft out of service for a period, in exchange for restoring its condition.

## Statistics dashboards

**Coming in a later update.**

What this is: airline-wide dashboards summarising how your virtual airline is performing — routes flown, on-time and landing-quality trends, fleet utilisation, and financial performance over time.

How to do it: a statistics section will be reachable from the main navigation, with your airline's key numbers laid out at a glance and the ability to drill into a specific route, aircraft, or pilot.
