package anytls

import (
	"time"

	mdata "github.com/go-gost/core/metadata"
	mdutil "github.com/go-gost/x/metadata/util"
)

type metadata struct {
	password         string
	handshakeTimeout time.Duration
}

func (l *anytlsListener) parseMetadata(md mdata.Metadata) error {
	l.md.password = mdutil.GetString(md, "password", "anytls.password")
	l.md.handshakeTimeout = mdutil.GetDuration(md, "handshakeTimeout", "anytls.handshakeTimeout")
	return nil
}
