//! VRChat's local OSC chatbox endpoint, mirroring what the WPF app sends:
//! `/chatbox/input <message> <sendImmediately>` over UDP to 127.0.0.1:9000.

pub const CHATBOX_LIMIT: usize = 144;
pub const DEFAULT_ADDRESS: &str = "127.0.0.1:9000";

fn pad(out: &mut Vec<u8>) {
    out.push(0);
    while out.len() % 4 != 0 {
        out.push(0);
    }
}

fn push_string(out: &mut Vec<u8>, value: &str) {
    out.extend_from_slice(value.as_bytes());
    pad(out);
}

/// Shortens a line at a word boundary so VRChat's 144-character limit is not
/// breached and the ellipsis is counted.
pub fn fit_chatbox_text(text: &str, limit: usize) -> String {
    if limit == 0 {
        return String::new();
    }
    let characters: Vec<char> = text.chars().collect();
    if characters.len() <= limit {
        return text.to_string();
    }

    let available = limit - 1;
    let slice = &characters[..available];
    let boundary = slice
        .iter()
        .rposition(|character| *character == ' ')
        .filter(|index| *index >= available / 2)
        .unwrap_or(available);
    let head: String = slice[..boundary].iter().collect();
    format!("{}…", head.trim_end())
}

/// A complete OSC 1.0 packet for `/chatbox/input`.
pub fn chatbox_input(text: &str, send_immediately: bool) -> Vec<u8> {
    let message = fit_chatbox_text(text, CHATBOX_LIMIT);
    let mut out = Vec::new();
    push_string(&mut out, "/chatbox/input");
    push_string(&mut out, if send_immediately { ",sT" } else { ",sF" });
    push_string(&mut out, &message);
    out
}

pub fn send(text: &str, send_immediately: bool) -> Result<(), String> {
    send_to(DEFAULT_ADDRESS, text, send_immediately)
}

pub fn send_to(address: &str, text: &str, send_immediately: bool) -> Result<(), String> {
    let message = fit_chatbox_text(text.trim(), CHATBOX_LIMIT);
    if message.is_empty() {
        return Err("Type a message first.".to_string());
    }

    let socket = std::net::UdpSocket::bind("127.0.0.1:0")
        .map_err(|error| format!("Could not open the OSC socket: {error}"))?;
    socket
        .send_to(&chatbox_input(&message, send_immediately), address)
        .map_err(|error| format!("Could not reach VRChat's OSC port at {address}: {error}"))?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn padded(value: &str) -> Vec<u8> {
        let mut out = Vec::new();
        push_string(&mut out, value);
        out
    }

    #[test]
    fn pads_osc_strings_to_four_byte_boundaries() {
        assert_eq!(padded("/chatbox/input"), b"/chatbox/input\0\0".to_vec());
        assert_eq!(padded("/chatbox/input").len() % 4, 0);
        assert_eq!(padded(",sT"), b",sT\0".to_vec());
        assert_eq!(padded("hi"), b"hi\0\0".to_vec());
        assert_eq!(padded("four"), b"four\0\0\0\0".to_vec());
    }

    #[test]
    fn builds_a_complete_chatbox_packet() {
        let packet = chatbox_input("hello", true);
        let expected = [
            b"/chatbox/input\0\0".as_slice(),
            b",sT\0".as_slice(),
            b"hello\0\0\0".as_slice(),
        ]
        .concat();
        assert_eq!(packet, expected);
        assert_eq!(packet.len() % 4, 0);
    }

    #[test]
    fn the_send_flag_selects_the_typetag() {
        let packet = chatbox_input("draft", false);
        assert!(packet.windows(3).any(|window| window == b",sF"));
    }

    #[test]
    fn short_text_is_left_alone() {
        assert_eq!(fit_chatbox_text("hello", CHATBOX_LIMIT), "hello");
        let exact = "x".repeat(CHATBOX_LIMIT);
        assert_eq!(fit_chatbox_text(&exact, CHATBOX_LIMIT), exact);
    }

    #[test]
    fn long_text_is_cut_at_a_word_boundary_with_an_ellipsis() {
        let text = "word ".repeat(60);
        let fitted = fit_chatbox_text(&text, CHATBOX_LIMIT);
        assert!(fitted.chars().count() <= CHATBOX_LIMIT);
        assert!(fitted.ends_with('…'));
        // The cut lands on the space before the ellipsis, not inside a word.
        assert!(fitted.trim_end_matches('…').ends_with("word"));
    }

    #[test]
    fn a_single_long_word_is_cut_at_the_limit() {
        let text = "a".repeat(200);
        let fitted = fit_chatbox_text(&text, CHATBOX_LIMIT);
        assert_eq!(fitted.chars().count(), CHATBOX_LIMIT);
        assert!(fitted.ends_with('…'));
    }

    #[test]
    fn an_empty_message_is_rejected() {
        assert!(send("   ", true).is_err());
    }

    #[test]
    fn sends_the_encoded_packet_over_udp() {
        let receiver =
            std::net::UdpSocket::bind("127.0.0.1:0").expect("bind a local osc receiver");
        receiver
            .set_read_timeout(Some(std::time::Duration::from_secs(2)))
            .expect("timeout");
        let address = receiver.local_addr().expect("local addr").to_string();

        send_to(&address, "  hi there  ", true).expect("send");

        let mut buffer = [0u8; 512];
        let size = receiver.recv(&mut buffer).expect("receive the osc packet");
        assert_eq!(&buffer[..size], chatbox_input("hi there", true).as_slice());
    }
}
