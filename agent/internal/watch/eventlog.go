package watch

import (
	"encoding/xml"
	"strings"
)

type xmlEvents struct {
	Events []struct {
		System struct {
			Provider struct {
				Name string `xml:"Name,attr"`
			} `xml:"Provider"`
			EventID     string `xml:"EventID"`
			Level       int    `xml:"Level"`
			TimeCreated struct {
				SystemTime string `xml:"SystemTime,attr"`
			} `xml:"TimeCreated"`
			EventRecordID uint64 `xml:"EventRecordID"`
			Channel       string `xml:"Channel"`
		} `xml:"System"`
		Rendering struct {
			Message string `xml:"Message"`
		} `xml:"RenderingInfo"`
	} `xml:"Event"`
}

// parseEvents reads "wevtutil qe ... /f:RenderedXml" output (Event elements with no common root).
func parseEvents(out string) ([]Event, error) {
	var x xmlEvents
	if err := xml.Unmarshal([]byte("<Events>"+out+"</Events>"), &x); err != nil {
		return nil, err
	}
	var evs []Event
	for _, e := range x.Events {
		sev := 4
		switch e.System.Level {
		case 1:
			sev = 2
		case 2:
			sev = 3
		}
		id := 0
		for _, c := range e.System.EventID {
			if c < '0' || c > '9' {
				break
			}
			id = id*10 + int(c-'0')
		}
		msg := strings.Join(strings.Fields(e.Rendering.Message), " ")
		evs = append(evs, Event{Log: e.System.Channel, Source: clip(e.System.Provider.Name, 120), ID: id, Severity: sev, Text: clip(msg, 1000), At: e.System.TimeCreated.SystemTime, Record: e.System.EventRecordID})
	}
	return evs, nil
}
