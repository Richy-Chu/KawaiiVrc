//! Minimal C ABI around Brave's `adblock-rust` engine.
//!
//! The engine is not `Send`/`Sync`, so callers must serialize all calls on one
//! thread. KawaiiVrc performs every WebView2 request callback on its UI
//! thread, which satisfies that requirement.

use std::ffi::{c_char, CStr, CString};
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::ptr;

use adblock::lists::{FilterSet, ParseOptions};
use adblock::request::Request;
use adblock::Engine;

pub struct EngineHandle {
    engine: Engine,
}

fn read_cstr(value: *const c_char) -> Option<String> {
    if value.is_null() {
        return None;
    }
    unsafe { CStr::from_ptr(value) }.to_str().ok().map(str::to_owned)
}

/// Build an engine from newline-separated adblock filter rules.
///
/// Returns a null pointer if the rules could not be loaded.
#[no_mangle]
pub extern "C" fn vrc_adblock_create(rules: *const c_char) -> *mut EngineHandle {
    let Some(rules) = read_cstr(rules) else {
        return ptr::null_mut();
    };

    let result = catch_unwind(AssertUnwindSafe(|| {
        let mut filter_set = FilterSet::new(false);
        filter_set.add_filter_list(rules, ParseOptions::default());
        let engine = Engine::new_with_filter_set(filter_set);
        Box::into_raw(Box::new(EngineHandle { engine }))
    }));

    result.unwrap_or(ptr::null_mut())
}

/// Rebuild an engine from a serialized engine produced by `Engine::serialize`.
///
/// Returns a null pointer when the serialized data is invalid.
#[no_mangle]
pub extern "C" fn vrc_adblock_create_from_serialized(
    data: *const u8,
    length: usize,
) -> *mut EngineHandle {
    if data.is_null() || length == 0 {
        return ptr::null_mut();
    }

    let result = catch_unwind(AssertUnwindSafe(|| {
        let bytes = unsafe { std::slice::from_raw_parts(data, length) };
        let mut engine = Engine::default();
        engine.deserialize(bytes).ok()?;
        Some(Box::into_raw(Box::new(EngineHandle { engine })))
    }));

    result.ok().flatten().unwrap_or(ptr::null_mut())
}

/// Load Brave's adblock-resources bundle (scriptlets, redirects, and
/// replacements) into an existing engine. Returns 1 on success, 0 on failure.
#[no_mangle]
pub extern "C" fn vrc_adblock_use_resources(
    handle: *mut EngineHandle,
    resources_json: *const c_char,
) -> i32 {
    if handle.is_null() {
        return 0;
    }
    let Some(resources_json) = read_cstr(resources_json) else {
        return 0;
    };

    let result = catch_unwind(AssertUnwindSafe(|| {
        let resources: Vec<adblock::resources::Resource> =
            match serde_json::from_str(&resources_json) {
                Ok(resources) => resources,
                Err(_) => return 0,
            };
        let engine = unsafe { &mut (*handle).engine };
        engine.use_resources(resources);
        1
    }));

    result.unwrap_or(0)
}

/// Check a request. Returns 1 when it should be blocked, 0 otherwise.
#[no_mangle]
pub extern "C" fn vrc_adblock_check(
    handle: *const EngineHandle,
    url: *const c_char,
    source_url: *const c_char,
    request_type: *const c_char,
) -> i32 {
    if handle.is_null() {
        return 0;
    }
    let (Some(url), Some(source_url), Some(request_type)) = (
        read_cstr(url),
        read_cstr(source_url),
        read_cstr(request_type),
    ) else {
        return 0;
    };

    let result = catch_unwind(AssertUnwindSafe(|| {
        let source = if source_url.is_empty() {
            url.as_str()
        } else {
            source_url.as_str()
        };
        let Ok(request) = Request::new(&url, source, &request_type, "GET") else {
            return 0;
        };
        let engine = unsafe { &(*handle).engine };
        if engine.check_network_request(&request).should_block() {
            1
        } else {
            0
        }
    }));

    result.unwrap_or(0)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn c(value: &str) -> CString {
        CString::new(value).unwrap()
    }

    #[test]
    fn blocks_and_unblocks_requests() {
        let rules = c("||doubleclick.net^\n||example.com/ads.js\n@@||example.com/ads.js$script");
        let engine = vrc_adblock_create(rules.as_ptr());
        assert!(!engine.is_null());

        let blocked = vrc_adblock_check(
            engine,
            c("https://doubleclick.net/pixel").as_ptr(),
            c("https://example.com/").as_ptr(),
            c("image").as_ptr(),
        );
        assert_eq!(blocked, 1);

        let allowed = vrc_adblock_check(
            engine,
            c("https://example.com/ads.js").as_ptr(),
            c("https://example.com/").as_ptr(),
            c("script").as_ptr(),
        );
        assert_eq!(allowed, 0);

        vrc_adblock_destroy(engine);
    }

    #[test]
    fn returns_cosmetic_resources() {
        let rules = c("example.com##.ad-banner\nexample.com##+js(set-constant, foo, bar)");
        let engine = vrc_adblock_create(rules.as_ptr());
        assert!(!engine.is_null());
        let json = vrc_adblock_cosmetic(engine, c("https://example.com/").as_ptr());
        assert!(!json.is_null());
        let text = unsafe { CStr::from_ptr(json) }.to_str().unwrap();
        assert!(text.contains("ad-banner"));
        assert!(text.contains("injectedScript"));
        vrc_adblock_free_string(json);
        vrc_adblock_destroy(engine);
    }
}

/// Check a request and return Brave's full blocker result as JSON.
///
/// The caller owns the returned string and must release it with
/// `vrc_adblock_free_string`.
#[no_mangle]
pub extern "C" fn vrc_adblock_check_json(
    handle: *const EngineHandle,
    url: *const c_char,
    source_url: *const c_char,
    request_type: *const c_char,
) -> *mut c_char {
    if handle.is_null() {
        return ptr::null_mut();
    }
    let (Some(url), Some(source_url), Some(request_type)) = (
        read_cstr(url),
        read_cstr(source_url),
        read_cstr(request_type),
    ) else {
        return ptr::null_mut();
    };

    let result = catch_unwind(AssertUnwindSafe(|| {
        let source = if source_url.is_empty() {
            url.as_str()
        } else {
            source_url.as_str()
        };
        let Ok(request) = Request::new(&url, source, &request_type, "GET") else {
            return ptr::null_mut();
        };
        let engine = unsafe { &(*handle).engine };
        let blocker = engine.check_network_request(&request);
        let csp = engine.get_csp_directives(&request);
        let payload = serde_json::json!({
            "blocked": blocker.should_block(),
            "important": blocker.important,
            "redirect": blocker.redirect,
            "rewrittenUrl": blocker.rewritten_url,
            "csp": csp,
        });
        CString::new(payload.to_string())
            .map(CString::into_raw)
            .unwrap_or(ptr::null_mut())
    }));

    result.ok().unwrap_or(ptr::null_mut())
}

/// Return the cosmetic filtering resources for a page as a JSON object.
///
/// The caller owns the returned string and must release it with
/// `vrc_adblock_free_string`.
#[no_mangle]
pub extern "C" fn vrc_adblock_cosmetic(
    handle: *const EngineHandle,
    page_url: *const c_char,
) -> *mut c_char {
    if handle.is_null() {
        return ptr::null_mut();
    }
    let Some(page_url) = read_cstr(page_url) else {
        return ptr::null_mut();
    };

    let result = catch_unwind(AssertUnwindSafe(|| {
        let engine = unsafe { &(*handle).engine };
        let resources = engine.url_cosmetic_resources(&page_url);
        let mut hide_selectors: Vec<String> = resources.hide_selectors.into_iter().collect();
        hide_selectors.sort();
        let mut exceptions: Vec<String> = resources.exceptions.into_iter().collect();
        exceptions.sort();
        let mut procedural_actions: Vec<String> =
            resources.procedural_actions.into_iter().collect();
        procedural_actions.sort();
        let payload = serde_json::json!({
            "hideSelectors": hide_selectors,
            "proceduralActions": procedural_actions,
            "exceptions": exceptions,
            "injectedScript": resources.injected_script,
            "genericHide": resources.generichide,
        });
        CString::new(payload.to_string()).ok().map(CString::into_raw)
    }));

    result.ok().flatten().unwrap_or(ptr::null_mut())
}

/// Release a string returned by `vrc_adblock_cosmetic`.
#[no_mangle]
pub extern "C" fn vrc_adblock_free_string(value: *mut c_char) {
    if value.is_null() {
        return;
    }
    unsafe {
        drop(CString::from_raw(value));
    }
}

/// Destroy an engine created by `vrc_adblock_create`.
#[no_mangle]
pub extern "C" fn vrc_adblock_destroy(handle: *mut EngineHandle) {
    if handle.is_null() {
        return;
    }
    unsafe {
        drop(Box::from_raw(handle));
    }
}
