package agentexperience

// SetRenderForTest replaces the writer of a lesson's block: for the tests that need a record that cannot be rendered.
func (l *Lessons) SetRenderForTest(render func(*ExperienceRecord, BlockOptions) (string, error)) {
	l.render = render
}
