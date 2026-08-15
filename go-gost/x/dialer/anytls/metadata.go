package anytls

import (
	"time"

	mdata "github.com/go-gost/core/metadata"
	mdutil "github.com/go-gost/x/metadata/util"
)

type metadata struct {
	password        string
	handshakeTimeout time.Duration
}

func (d *anytlsDialer) parseMetadata(md mdata.Metadata) error {
	d.md.password = mdutil.GetString(md, "password", "anytls.password")
	d.md.handshakeTimeout = mdutil.GetDuration(md, "handshakeTimeout", "anytls.handshakeTimeout")
	return nil
}
