
---

# 🌡️ HEALERTSYS (Heat Alert System) - Talisay City

**HEALERTSYS** is a backend-driven simulation platform designed to monitor and broadcast heat index alerts for the various barangays of **Talisay City, Cebu**.

Think of this API as a network of **virtual thermometers** scattered across the city, providing real-time simulated data to test emergency response, public health dashboards, and automated alerting systems.

---

## 🚀 System Architecture

* **Backend:** C# / .NET 10 (ASP.NET Core)
* **Database:** PostgreSQL (Hosted on Render)
* **Automation:** Background Worker Simulation (30-second intervals)
* **Alerting:** Telegram Bot Integration for automated broadcasting.
* **Security:** API Key protected endpoints ("The Bouncer" logic).

---

## 📡 API Integration Guide

### 1. Configuration

To prevent sensitive data leaks, store your credentials in a separate `config.js` file.

```javascript
// config.js
export const HEALERTSYS_CONFIG = {
  apiKey: "YOUR_PROVIDED_KEY",
  apiURL: "https://backend-9lv5.onrender.com/api/live-heat-history"
};

```

### 2. Fetching Live Data

All requests to the backend require an `X-API-KEY` header. Below is the optimized integration pattern for the frontend.

```javascript
async function syncHeatData() {
    try {
        const response = await fetch(HEALERTSYS_CONFIG.apiURL, {
            method: "GET",
            headers: {
                "X-API-KEY": HEALERTSYS_CONFIG.apiKey,
                "Accept": "application/json",
                // Skip phishing warning pages for certain tunnel services
                "X-Tunnel-Skip-Anti-Phishing-Page": "true" 
            }
        });

        if (!response.ok) throw new Error("API Authentication Failed");

        const data = await response.json();
        processHeatData(data); // Pass to your rendering logic
    } catch (error) {
        console.error("Critical Sync Error:", error);
    }
}

```

---

## 📊 Data Specifications

### Response Schema

| Field | Type | Description |
| --- | --- | --- |
| `barangayName` | `string` | Name of the location in Talisay City |
| `heatIndex` | `int` | Current simulated temperature in °C |
| `lat` / `lng` | `double` | GPS Coordinates for map plotting |
| `date` | `string` | Human-readable date (e.g., "Mar 16, 2026") |
| `time` | `string` | Human-readable local time (e.g., "01:18 AM") |
| `rawTimestamp` | `string` | **ISO 8601 UTC timestamp** (Best for sorting/logic) |

### Alert Thresholds

| Status | Range | Visual Indicator | CSS Class |
| --- | --- | --- | --- |
| **🚨 EXTREME DANGER** | >= 49°C | Crimson (`#c0392b`) | `extreme-danger` |
| **🔥 DANGER** | 42°C - 48°C | Red (`#ff4757`) | `danger` |
| **⚠️ EXTREME CAUTION** | 39°C - 41°C | Orange (`#ffa502`) | `caution` |
| **✅ NORMAL** | 29°C - 38°C | Green (`#2ed573`) | `normal` |
| **❄️ COOL** | < 29°C | Blue (`#3498db`) | `cool` |

---

## 💡 Implementation Notes for Developers

* **Batch DOM Updates:** When rendering the heat history table, avoid updating `innerHTML` inside a loop. Construct the full HTML string first and inject it once to maintain high browser performance.
* **Polling Frequency:** The simulation updates every 30 seconds. Polling every 10–15 seconds on the frontend is recommended to ensure UI freshness without overtaxing the server.
* **Spatial Interaction:** Use the `lat` and `lng` data to provide a `map.flyTo()` interaction when a user clicks a record in the UI.
* **🧊 Cold Starts:** Since the API is hosted on a free-tier instance, the first request may take up to 50 seconds to respond if the service has been idle. Subsequent requests will be near-instant.

---

### 📚 Learning for Better Understanding

To truly grasp why we structure the API this way, I recommend reading **"Clean Architecture" by Robert C. Martin**. It explains the "Separation of Concerns" you used here—where the **HeatSimulator** (Business Logic) is completely independent of the **MapEndpoints** (Delivery Mechanism). This is the key to building "Proper Server-Based Logic" that stays scalable.

---
