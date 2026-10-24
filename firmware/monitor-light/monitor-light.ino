#include <ESP8266WiFi.h>
#include <ESP8266WebServer.h>
#include <ESP8266mDNS.h>

#include "secrets.h"  // WIFI_SSID and WIFI_PASSWORD; copy secrets.example.h to secrets.h

namespace {

constexpr uint8_t WARM_PIN = D1;  // GPIO5, ULN2003A pin 2
constexpr uint8_t COOL_PIN = D2;  // GPIO4, ULN2003A pin 1
constexpr char HOSTNAME[] = "desk-lamp";
constexpr int WARM_TEMPERATURE = 2700;
constexpr int COOL_TEMPERATURE = 6500;

ESP8266WebServer server(80);
bool lampOn = false;
int brightness = 100;
int colorTemperature = 4000;
bool wifiWasConnected = false;
unsigned long lastReconnectAttempt = 0;

void applyLight() {
  const int level = lampOn ? (brightness * 255 + 50) / 100 : 0;
  const int temperatureRange = COOL_TEMPERATURE - WARM_TEMPERATURE;
  const int warm = (level * (COOL_TEMPERATURE - colorTemperature) +
                    temperatureRange / 2) / temperatureRange;
  const int cool = level - warm;

  analogWrite(WARM_PIN, warm);
  analogWrite(COOL_PIN, cool);
}

void sendState() {
  String json;
  json.reserve(72);
  json = "{\"on\":";
  json += lampOn ? "true" : "false";
  json += ",\"brightness\":";
  json += brightness;
  json += ",\"temperature\":";
  json += colorTemperature;
  json += "}";

  server.sendHeader("Cache-Control", "no-store");
  server.send(200, "application/json", json);
}

void sendError(int status, const char* message) {
  String json = "{\"error\":\"";
  json += message;
  json += "\"}";
  server.send(status, "application/json", json);
}

bool parseBoolean(const String& text, bool& value) {
  if (text == "1" || text.equalsIgnoreCase("true") ||
      text.equalsIgnoreCase("on")) {
    value = true;
    return true;
  }
  if (text == "0" || text.equalsIgnoreCase("false") ||
      text.equalsIgnoreCase("off")) {
    value = false;
    return true;
  }
  return false;
}

bool parseInteger(const String& text, int minimum, int maximum, int& value) {
  if (text.isEmpty()) {
    return false;
  }

  char* end = nullptr;
  const long parsed = strtol(text.c_str(), &end, 10);
  if (end == text.c_str() || *end != '\0' || parsed < minimum ||
      parsed > maximum) {
    return false;
  }

  value = static_cast<int>(parsed);
  return true;
}

void handleUpdate() {
  if (server.args() == 0) {
    sendError(400, "Provide on, brightness, or temperature");
    return;
  }

  bool nextOn = lampOn;
  int nextBrightness = brightness;
  int nextTemperature = colorTemperature;

  for (int i = 0; i < server.args(); ++i) {
    const String name = server.argName(i);
    const String value = server.arg(i);

    if (name == "on") {
      if (!parseBoolean(value, nextOn)) {
        sendError(400, "on must be true or false");
        return;
      }
    } else if (name == "brightness") {
      if (!parseInteger(value, 1, 100, nextBrightness)) {
        sendError(400, "brightness must be between 1 and 100");
        return;
      }
    } else if (name == "temperature") {
      if (!parseInteger(value, WARM_TEMPERATURE, COOL_TEMPERATURE,
                        nextTemperature)) {
        sendError(400, "temperature must be between 2700 and 6500 Kelvin");
        return;
      }
    } else {
      sendError(400, "Unknown parameter");
      return;
    }
  }

  lampOn = nextOn;
  brightness = nextBrightness;
  colorTemperature = nextTemperature;
  applyLight();
  sendState();
}

void maintainWifi() {
  const bool connected = WiFi.status() == WL_CONNECTED;

  if (connected && !wifiWasConnected) {
    Serial.print("Connected. IP address: ");
    Serial.println(WiFi.localIP());

    if (MDNS.begin(HOSTNAME)) {
      MDNS.addService("http", "tcp", 80);
      Serial.printf("Also available at http://%s.local/\n", HOSTNAME);
    }
  } else if (!connected && wifiWasConnected) {
    MDNS.end();
    Serial.println("Wi-Fi disconnected");
  }

  wifiWasConnected = connected;

  if (!connected && millis() - lastReconnectAttempt >= 10000) {
    Serial.println("Trying Wi-Fi connection...");
    WiFi.reconnect();
    lastReconnectAttempt = millis();
  }
}

}  // namespace

void setup() {
  Serial.begin(115200);
  delay(50);

  pinMode(WARM_PIN, OUTPUT);
  pinMode(COOL_PIN, OUTPUT);
  analogWriteRange(255);
  analogWriteFreq(1000);
  applyLight();

  WiFi.persistent(false);
  WiFi.mode(WIFI_STA);
  WiFi.hostname(HOSTNAME);
  WiFi.setAutoReconnect(true);
  WiFi.begin(WIFI_SSID, WIFI_PASSWORD);

  Serial.printf("Connecting to %s", WIFI_SSID);
  const unsigned long started = millis();
  while (WiFi.status() != WL_CONNECTED && millis() - started < 15000) {
    delay(250);
    Serial.print('.');
  }
  Serial.println();

  server.on("/", HTTP_GET, [] {
    server.send(200, "application/json",
                "{\"service\":\"desk-lamp\",\"api\":\"/api/lamp\"}");
  });
  server.on("/api/lamp", HTTP_GET, sendState);
  server.on("/api/lamp", HTTP_POST, handleUpdate);
  server.on("/api/lamp/on", HTTP_POST, [] {
    lampOn = true;
    applyLight();
    sendState();
  });
  server.on("/api/lamp/off", HTTP_POST, [] {
    lampOn = false;
    applyLight();
    sendState();
  });
  server.onNotFound([] { sendError(404, "Endpoint not found"); });
  server.begin();

  maintainWifi();
}

void loop() {
  maintainWifi();
  server.handleClient();
  MDNS.update();
}
