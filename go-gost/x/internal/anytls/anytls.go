package anytls

import (
	"context"
	"crypto/sha256"
	"crypto/tls"
	"encoding/binary"
	"errors"
	"fmt"
	"io"
	"net"
	"strconv"
	"sync"
	"sync/atomic"
	"time"
)

const (
	cmdWaste               = 0
	cmdSYN                 = 1
	cmdPSH                 = 2
	cmdFIN                 = 3
	cmdSettings            = 4
	cmdAlert               = 5
	cmdUpdatePaddingScheme = 6
	cmdSYNACK              = 7
	cmdServerSettings      = 10
	frameHeaderSize        = 7
	maxFrameSize           = 65535
	defaultHandshakeLimit  = 10 * time.Second
)

var errSessionClosed = errors.New("anytls: session closed")

// Client is a small anytls client used by the GOST dialer. It keeps one
// multiplexed session per configured destination.
type Client struct {
	password string
	tls      *tls.Config
	dialOut  func(context.Context) (net.Conn, error)

	mu      sync.Mutex
	session *session
}

func NewClient(password string, tlsConfig *tls.Config, dialOut func(context.Context) (net.Conn, error)) *Client {
	return &Client{password: password, tls: tlsConfig, dialOut: dialOut}
}

func (c *Client) CreateProxy(ctx context.Context, destination string) (net.Conn, error) {
	c.mu.Lock()
	s := c.session
	if s == nil || s.isClosed() {
		var err error
		s, err = c.newSession(ctx)
		if err != nil {
			c.mu.Unlock()
			return nil, err
		}
		c.session = s
	}
	c.mu.Unlock()

	stream, err := s.openStream()
	if err != nil {
		c.mu.Lock()
		if c.session == s {
			c.session = nil
		}
		c.mu.Unlock()
		s.close()
		return nil, err
	}
	if err := writeSocksAddr(stream, destination); err != nil {
		_ = stream.Close()
		return nil, err
	}
	return stream, nil
}

func (c *Client) Close() error {
	c.mu.Lock()
	s := c.session
	c.session = nil
	c.mu.Unlock()
	if s != nil {
		return s.close()
	}
	return nil
}

func (c *Client) newSession(ctx context.Context) (*session, error) {
	if c.password == "" || c.dialOut == nil {
		return nil, errors.New("anytls: password and dialer are required")
	}
	conn, err := c.dialOut(ctx)
	if err != nil {
		return nil, err
	}
	config := &tls.Config{InsecureSkipVerify: true}
	if c.tls != nil {
		config = c.tls.Clone()
	}
	tlsConn := tls.Client(conn, config)
	if err := tlsConn.HandshakeContext(ctx); err != nil {
		_ = conn.Close()
		return nil, err
	}
	if err := writeAuth(tlsConn, c.password); err != nil {
		_ = tlsConn.Close()
		return nil, err
	}
	s := newSession(tlsConn, true, nil)
	if _, err := s.writeFrame(cmdSettings, 0, []byte("v=2\nclient=RelayForge\n")); err != nil {
		_ = tlsConn.Close()
		return nil, err
	}
	go s.readLoop()
	return s, nil
}

// Server accepts authenticated anytls sessions and emits one net.Conn for
// each multiplexed stream. The stream destination is consumed before the
// connection is emitted, leaving the wrapped application protocol untouched.
type Server struct {
	password string
	tls      *tls.Config
}

func NewServer(password string, tlsConfig *tls.Config) *Server {
	return &Server{password: password, tls: tlsConfig}
}

func (s *Server) Handle(ctx context.Context, raw net.Conn, onStream func(net.Conn)) {
	if s.password == "" || onStream == nil {
		_ = raw.Close()
		return
	}
	config := &tls.Config{}
	if s.tls != nil {
		config = s.tls.Clone()
	}
	tlsConn := tls.Server(raw, config)
	_ = tlsConn.SetDeadline(time.Now().Add(defaultHandshakeLimit))
	if err := tlsConn.HandshakeContext(ctx); err != nil {
		_ = raw.Close()
		return
	}
	_ = tlsConn.SetDeadline(time.Time{})
	if err := readAuth(tlsConn, s.password); err != nil {
		_ = tlsConn.Close()
		return
	}
	ss := newSession(tlsConn, false, onStream)
	ss.readLoop()
}

type session struct {
	conn    net.Conn
	client  bool
	onStream func(net.Conn)

	writeMu sync.Mutex
	streamMu sync.Mutex
	streams map[uint32]*stream
	nextID  atomic.Uint32
	done    chan struct{}
	closeOnce sync.Once
}

func newSession(conn net.Conn, client bool, onStream func(net.Conn)) *session {
	return &session{
		conn: conn, client: client, onStream: onStream,
		streams: make(map[uint32]*stream), done: make(chan struct{}),
	}
}

func (s *session) isClosed() bool {
	select {
	case <-s.done:
		return true
	default:
		return false
	}
}

func (s *session) close() error {
	s.closeOnce.Do(func() {
		close(s.done)
		_ = s.conn.Close()
		s.streamMu.Lock()
		for _, stream := range s.streams {
			stream.remoteClose()
		}
		s.streams = make(map[uint32]*stream)
		s.streamMu.Unlock()
	})
	return nil
}

func (s *session) readLoop() {
	defer s.close()
	var header [frameHeaderSize]byte
	for {
		if _, err := io.ReadFull(s.conn, header[:]); err != nil {
			return
		}
		cmd := header[0]
		sid := binary.BigEndian.Uint32(header[1:5])
		length := int(binary.BigEndian.Uint16(header[5:7]))
		data := make([]byte, length)
		if _, err := io.ReadFull(s.conn, data); err != nil {
			return
		}
		switch cmd {
		case cmdPSH:
			s.streamMu.Lock()
			stream := s.streams[sid]
			s.streamMu.Unlock()
			if stream != nil {
				stream.push(data)
			}
		case cmdSYN:
			if s.client {
				continue
			}
			stream := newStream(s, sid)
			s.streamMu.Lock()
			s.streams[sid] = stream
			s.streamMu.Unlock()
			go s.acceptStream(stream)
		case cmdFIN:
			s.streamMu.Lock()
			stream := s.streams[sid]
			delete(s.streams, sid)
			s.streamMu.Unlock()
			if stream != nil {
				stream.remoteClose()
			}
		case cmdSettings:
			if !s.client {
				_, _ = s.writeFrame(cmdServerSettings, 0, []byte("v=2\n"))
			}
		case cmdSYNACK, cmdServerSettings, cmdWaste, cmdUpdatePaddingScheme:
			// Version negotiation and padding frames do not need local state.
		case cmdAlert:
			return
		default:
			return
		}
	}
}

func (s *session) acceptStream(stream *stream) {
	if err := readSocksAddr(stream); err != nil {
		_ = stream.Close()
		return
	}
	if s.onStream == nil {
		_ = stream.Close()
		return
	}
	s.onStream(stream)
}

func (s *session) openStream() (*stream, error) {
	if s.isClosed() {
		return nil, errSessionClosed
	}
	id := s.nextID.Add(1)
	stream := newStream(s, id)
	s.streamMu.Lock()
	s.streams[id] = stream
	s.streamMu.Unlock()
	if _, err := s.writeFrame(cmdSYN, id, nil); err != nil {
		s.streamMu.Lock()
		delete(s.streams, id)
		s.streamMu.Unlock()
		return nil, err
	}
	return stream, nil
}

func (s *session) writeFrame(cmd byte, sid uint32, data []byte) (int, error) {
	if len(data) > maxFrameSize {
		return 0, errors.New("anytls: control frame too large")
	}
	s.writeMu.Lock()
	defer s.writeMu.Unlock()
	select {
	case <-s.done:
		return 0, errSessionClosed
	default:
	}
	var header [frameHeaderSize]byte
	header[0] = cmd
	binary.BigEndian.PutUint32(header[1:5], sid)
	binary.BigEndian.PutUint16(header[5:7], uint16(len(data)))
	if _, err := s.conn.Write(header[:]); err != nil {
		return 0, err
	}
	if len(data) > 0 {
		if _, err := s.conn.Write(data); err != nil {
			return 0, err
		}
	}
	return len(data), nil
}

func (s *session) writeData(id uint32, data []byte) (int, error) {
	if len(data) == 0 {
		return 0, nil
	}
	written := 0
	for written < len(data) {
		end := written + maxFrameSize
		if end > len(data) {
			end = len(data)
		}
		if _, err := s.writeFrame(cmdPSH, id, data[written:end]); err != nil {
			return written, err
		}
		written = end
	}
	return written, nil
}

type stream struct {
	session *session
	id      uint32
	readCh  chan []byte
	done    chan struct{}
	closeOnce sync.Once
	readMu sync.Mutex
	readBuf []byte
}

func newStream(s *session, id uint32) *stream {
	return &stream{session: s, id: id, readCh: make(chan []byte, 32), done: make(chan struct{})}
}

func (s *stream) Read(p []byte) (int, error) {
	s.readMu.Lock()
	if len(s.readBuf) > 0 {
		n := copy(p, s.readBuf)
		s.readBuf = s.readBuf[n:]
		s.readMu.Unlock()
		return n, nil
	}
	s.readMu.Unlock()
	select {
	case data, ok := <-s.readCh:
		if !ok {
			return 0, io.EOF
		}
		s.readMu.Lock()
		n := copy(p, data)
		if n < len(data) {
			s.readBuf = append(s.readBuf[:0], data[n:]...)
		}
		s.readMu.Unlock()
		return n, nil
	case <-s.done:
		return 0, io.EOF
	}
}

func (s *stream) Write(p []byte) (int, error) {
	return s.session.writeData(s.id, p)
}

func (s *stream) Close() error {
	s.closeOnce.Do(func() {
		close(s.done)
		_, _ = s.session.writeFrame(cmdFIN, s.id, nil)
		s.session.streamMu.Lock()
		delete(s.session.streams, s.id)
		s.session.streamMu.Unlock()
	})
	return nil
}

func (s *stream) push(data []byte) {
	select {
	case s.readCh <- data:
	case <-s.done:
	case <-s.session.done:
	}
}

func (s *stream) remoteClose() {
	s.closeOnce.Do(func() {
		close(s.done)
	})
}

func (s *stream) LocalAddr() net.Addr  { return s.session.conn.LocalAddr() }
func (s *stream) RemoteAddr() net.Addr { return s.session.conn.RemoteAddr() }
func (s *stream) SetDeadline(t time.Time) error {
	return s.SetReadDeadline(t)
}
func (s *stream) SetReadDeadline(t time.Time) error {
	return s.session.conn.SetReadDeadline(t)
}
func (s *stream) SetWriteDeadline(t time.Time) error {
	return s.session.conn.SetWriteDeadline(t)
}

func writeAuth(w io.Writer, password string) error {
	hash := sha256.Sum256([]byte(password))
	var length [2]byte
	return writeAll(w, hash[:], length[:])
}

func readAuth(r io.Reader, password string) error {
	var expected [32]byte
	expected = sha256.Sum256([]byte(password))
	var actual [32]byte
	if _, err := io.ReadFull(r, actual[:]); err != nil || actual != expected {
		return errors.New("anytls: authentication failed")
	}
	var length [2]byte
	if _, err := io.ReadFull(r, length[:]); err != nil {
		return err
	}
	padding := int(binary.BigEndian.Uint16(length[:]))
	if padding == 0 {
		return nil
	}
	_, err := io.CopyN(io.Discard, r, int64(padding))
	return err
}

func writeAll(w io.Writer, chunks ...[]byte) error {
	for _, chunk := range chunks {
		if _, err := w.Write(chunk); err != nil {
			return err
		}
	}
	return nil
}

func writeSocksAddr(w io.Writer, address string) error {
	host, portText, err := net.SplitHostPort(address)
	if err != nil {
		return err
	}
	port, err := strconv.ParseUint(portText, 10, 16)
	if err != nil {
		return err
	}
	if ip := net.ParseIP(host); ip != nil {
		if ip4 := ip.To4(); ip4 != nil {
			data := make([]byte, 7)
			data[0] = 1
			copy(data[1:5], ip4)
			binary.BigEndian.PutUint16(data[5:], uint16(port))
			_, err = w.Write(data)
			return err
		}
		data := make([]byte, 19)
		data[0] = 4
		copy(data[1:17], ip.To16())
		binary.BigEndian.PutUint16(data[17:], uint16(port))
		_, err = w.Write(data)
		return err
	}
	if len(host) > 255 {
		return errors.New("anytls: domain name too long")
	}
	data := make([]byte, 4+len(host))
	data[0] = 3
	data[1] = byte(len(host))
	copy(data[2:], host)
	binary.BigEndian.PutUint16(data[2+len(host):], uint16(port))
	_, err = w.Write(data)
	return err
}

func readSocksAddr(r io.Reader) error {
	var atyp [1]byte
	if _, err := io.ReadFull(r, atyp[:]); err != nil {
		return err
	}
	var addressLen int
	switch atyp[0] {
	case 1:
		addressLen = 4
	case 4:
		addressLen = 16
	case 3:
		var length [1]byte
		if _, err := io.ReadFull(r, length[:]); err != nil {
			return err
		}
		addressLen = int(length[0])
	default:
		return fmt.Errorf("anytls: unknown address type %d", atyp[0])
	}
	if _, err := io.CopyN(io.Discard, r, int64(addressLen+2)); err != nil {
		return err
	}
	return nil
}
