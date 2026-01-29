Käynnistä API:
dotnet run --urls http://localhost:5012

Luo varaus:
Invoke-RestMethod `
  -Method POST `
  -Uri http://localhost:5012/bookings `
  -ContentType "application/json" `
  -Body '{
    "roomId": "A101",
    "reservedBy": "Matti",
    "start": "2026-06-01T10:00:00+02:00",
    "end": "2026-06-01T12:00:00+02:00"
  }'

Listaa huoneen varaukset:
Invoke-RestMethod http://localhost:5012/rooms/A101/bookings

Peruuta varaus:
Invoke-RestMethod `
  -Method DELETE `
  -Uri http://localhost:5012/bookings/<ID>

Hae vapaat ajat:

start = hakuvälin alku

end = hakuvälin loppu

minHours = minimikesto tunneissa

Invoke-RestMethod `
  "http://localhost:5012/rooms/A101/free-slots?start=2027-05-06T00:00:00%2B02:00&end=2027-05-08T00:00:00%2B02:00&minHours=2"

Paremmat vikaviestit saa kun lisää tämän PowerHelliin:
function Post-Booking($url, $json) {
  try {
    Invoke-RestMethod -Method POST -Uri $url -ContentType "application/json" -Body $json
  } catch {
    "Status: $($_.Exception.Response.StatusCode.value__)"
    $reader = New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream())
    $reader.ReadToEnd()
  }
}

Testaa onko API käynnissä:
Test-NetConnection localhost -Port 5012
